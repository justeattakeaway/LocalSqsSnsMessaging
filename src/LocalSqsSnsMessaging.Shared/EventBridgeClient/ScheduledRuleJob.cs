using System.Globalization;
using System.Text.Json.Nodes;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Fires a scheduled rule on the bus's <see cref="TimeProvider"/>: at each time its
/// <see cref="EventBridgeSchedule"/> names, an enabled rule's targets receive a
/// <c>Scheduled Event</c> from <c>aws.events</c>. A disabled rule keeps its cadence but delivers
/// nothing. Disposing the job (when the rule is deleted or rescheduled) stops its timer.
/// </summary>
internal sealed class ScheduledRuleJob : IDisposable
{
    private readonly object _fireLock = new();
    private readonly RuleResource _rule;
    private readonly EventBridgeSchedule _schedule;
    private readonly InMemoryAwsBus _bus;
    private readonly ITimer _timer;
    private DateTimeOffset? _next;
    private bool _disposed;

    private ScheduledRuleJob(RuleResource rule, string expression, EventBridgeSchedule schedule, InMemoryAwsBus bus)
    {
        _rule = rule;
        Expression = expression;
        _schedule = schedule;
        _bus = bus;
        _next = schedule.NextAfter(bus.TimeProvider.GetUtcNow());
        _timer = bus.TimeProvider.CreateTimer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Arm();
    }

    /// <summary>The schedule expression this job runs.</summary>
    public string Expression { get; }

    /// <summary>Starts firing <paramref name="rule"/> on its schedule; null if the expression cannot be read.</summary>
    public static ScheduledRuleJob? Start(RuleResource rule, string expression, InMemoryAwsBus bus) =>
        EventBridgeSchedule.TryParse(expression, out var schedule) ? new ScheduledRuleJob(rule, expression, schedule, bus) : null;

    public void Dispose()
    {
        lock (_fireLock)
        {
            _disposed = true;
            _timer.Dispose();
        }
    }

    /// <summary>
    /// The longest wait a timer accepts (<c>uint.MaxValue - 1</c> milliseconds, about 49.7 days). A
    /// firing further off than that (<c>rate(50 days)</c>, a yearly cron) is reached in several waits.
    /// </summary>
    private static readonly TimeSpan MaxTimerDue = TimeSpan.FromMilliseconds(uint.MaxValue - 1.0);

    private void Arm()
    {
        if (_next is { } next)
        {
            var due = next - _bus.TimeProvider.GetUtcNow();
            _timer.Change(due <= TimeSpan.Zero ? TimeSpan.Zero : due < MaxTimerDue ? due : MaxTimerDue, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_fireLock)
        {
            if (_disposed || _next is not { } firedAt)
            {
                return;
            }

            if (_bus.TimeProvider.GetUtcNow() < firedAt)
            {
                // Woken by a capped wait before the firing is due: wait the rest.
                Arm();
                return;
            }

            if (_rule.IsEnabled)
            {
                EventBridgeEventDelivery.DeliverToRule(_rule, new EventBridgeEvent { Envelope = ScheduledEvent(firedAt), IngestionTime = firedAt }, _bus);
            }

            _next = _schedule.NextAfter(firedAt);
            Arm();
        }
    }

    /// <summary>The envelope EventBridge delivers for a scheduled rule's firing.</summary>
    private JsonObject ScheduledEvent(DateTimeOffset time) => new()
    {
        ["version"] = "0",
        ["id"] = Guid.NewGuid().ToString(),
        ["detail-type"] = "Scheduled Event",
        ["source"] = "aws.events",
        ["account"] = _bus.CurrentAccountId,
        ["time"] = time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        ["region"] = InMemoryAwsBus.ArnRegion(_rule.Arn) ?? _bus.CurrentRegion,
        ["resources"] = new JsonArray(_rule.Arn),
        ["detail"] = new JsonObject()
    };
}
