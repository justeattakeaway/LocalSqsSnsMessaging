using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalSqsSnsMessaging;

/// <summary>
/// In-memory representation of an EventBridge replay, and the job that runs it on the bus's
/// <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>
/// A replay answers STARTING, turns RUNNING one <see cref="StepInterval"/> later (when it re-emits
/// the archived events in its window into the destination bus, each carrying a
/// <c>replay-name</c> field), and is COMPLETED one step after that. CancelReplay moves a STARTING
/// or RUNNING replay to CANCELLING, and the next step settles it CANCELLED. A replay's timer is
/// disposed as soon as it reaches a terminal state.
/// </remarks>
internal sealed class ReplayResource
{
    /// <summary>How long a replay stays in each of its transient states.</summary>
    public static readonly TimeSpan StepInterval = TimeSpan.FromSeconds(1);

    public const string Starting = "STARTING";
    public const string Running = "RUNNING";
    public const string Completed = "COMPLETED";
    public const string Cancelling = "CANCELLING";
    public const string Cancelled = "CANCELLED";

    private readonly object _stateLock = new();
    private ITimer? _timer;

    public required string Name { get; init; }
    public required string Arn { get; init; }
    public string? Description { get; init; }

    /// <summary>The ARN of the archive the replay reads, as given to StartReplay.</summary>
    public required string EventSourceArn { get; init; }
    public required ArchiveResource Archive { get; init; }

    /// <summary>The ARN of the event bus the replay re-emits into, as given to StartReplay.</summary>
    public required string DestinationArn { get; init; }
    public required string DestinationBusName { get; init; }

    /// <summary>The rules the replay is limited to, or null for every rule on the destination bus.</summary>
    public IReadOnlyList<string>? FilterArns { get; init; }
    public required DateTimeOffset EventStartTime { get; init; }
    public required DateTimeOffset EventEndTime { get; init; }
    public required InMemoryAwsBus Bus { get; init; }

    public string State { get; private set; } = Starting;
    public DateTimeOffset ReplayStartTime { get; private set; }
    public DateTimeOffset? ReplayEndTime { get; private set; }

    /// <summary>The event time of the last event the replay re-emitted; null until it has re-emitted one.</summary>
    public DateTimeOffset? EventLastReplayedTime { get; private set; }

    /// <summary>Starts the replay: records its start time and schedules its first step.</summary>
    public void Start()
    {
        var timeProvider = Bus.TimeProvider;
        ReplayStartTime = timeProvider.GetUtcNow();
        _timer = timeProvider.CreateTimer(_ => Step(), null, StepInterval, StepInterval);
    }

    /// <summary>Moves a STARTING or RUNNING replay to CANCELLING; false when it is in any other state.</summary>
    public bool TryCancel()
    {
        lock (_stateLock)
        {
            if (State is not (Starting or Running))
            {
                return false;
            }
            State = Cancelling;
            return true;
        }
    }

    private void Step()
    {
        lock (_stateLock)
        {
            switch (State)
            {
                case Starting:
                    State = Running;
                    ReplayEvents();
                    break;
                case Running:
                    Finish(Completed);
                    break;
                case Cancelling:
                    Finish(Cancelled);
                    break;
            }
        }
    }

    private void ReplayEvents()
    {
        if (!Bus.RegionOfArn(DestinationArn).EventBuses.TryGetValue(DestinationBusName, out var destination))
        {
            return;
        }

        Func<RuleResource, bool>? ruleFilter = FilterArns is { Count: > 0 } filterArns
            ? rule => filterArns.Contains(rule.Arn, StringComparer.Ordinal)
            : null;

        foreach (var archived in Archive.EventsBetween(EventStartTime, EventEndTime, Bus.TimeProvider.GetUtcNow()))
        {
            if (JsonNode.Parse(archived.Envelope, documentOptions: new JsonDocumentOptions { MaxDepth = ArchiveResource.MaxEnvelopeDepth }) is not JsonObject envelope)
            {
                continue;
            }
            envelope["replay-name"] = Name;
            // A replayed event is ingested again, now, and carries no trace header.
            var replayed = new EventBridgeEvent { Envelope = envelope, IngestionTime = Bus.TimeProvider.GetUtcNow() };
            EventBridgeEventDelivery.Route(destination, replayed, Bus, ruleFilter);
            EventLastReplayedTime = archived.Time;
        }
    }

    private void Finish(string terminalState)
    {
        State = terminalState;
        ReplayEndTime = Bus.TimeProvider.GetUtcNow();
        _timer?.Dispose();
    }
}
