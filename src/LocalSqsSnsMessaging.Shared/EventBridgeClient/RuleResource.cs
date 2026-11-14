using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

/// <summary>
/// In-memory representation of an EventBridge rule and its targets.
/// </summary>
internal sealed class RuleResource
{
    private readonly object _targetsGate = new();
    private bool _deleted;

    public required string Name { get; init; }
    public required string Arn { get; init; }
    public required string EventBusName { get; init; }
    public string? EventPattern { get; set; }
    public string? ScheduleExpression { get; set; }

    /// <summary>Rule state: "ENABLED" or "DISABLED".</summary>
    public string State { get; set; } = "ENABLED";
    public string? Description { get; set; }
    public string? RoleArn { get; set; }

    public bool IsEnabled => string.Equals(State, "ENABLED", StringComparison.Ordinal);

    /// <summary>The rule's tags, set when the rule is created (a PutRule that updates it ignores Tags).</summary>
    public EventBridgeTagSet Tags { get; init; } = new();

    /// <summary>
    /// Targets attached to this rule, in insertion order, with unique ids. A snapshot: every change
    /// replaces the whole list, so a reader (such as delivery) never sees a half-applied change.
    /// </summary>
    public IReadOnlyList<Target> Targets { get; private set; } = [];

    /// <summary>
    /// Adds <paramref name="targets"/>, replacing in place any existing target with the same id.
    /// All or nothing: refused with <c>LimitExceededException</c> when the rule would hold more than
    /// <see cref="EventBridgeTargetValidation.MaxTargetsPerRule"/>, and with
    /// <c>ResourceNotFoundException</c> when the rule was deleted meanwhile.
    /// </summary>
    public void PutTargets(IReadOnlyList<Target> targets)
    {
        lock (_targetsGate)
        {
            if (_deleted)
            {
                throw NotFound(Name, EventBusName);
            }

            var updated = Targets.ToList();
            foreach (var target in targets)
            {
                var index = updated.FindIndex(t => string.Equals(t.Id, target.Id, StringComparison.Ordinal));
                if (index >= 0)
                {
                    updated[index] = target;
                }
                else
                {
                    updated.Add(target);
                }
            }

            if (updated.Count > EventBridgeTargetValidation.MaxTargetsPerRule)
            {
                throw new InternalLimitExceededException();
            }

            Targets = updated;
        }
    }

    /// <summary>Removes the targets with the given ids; ids the rule does not hold are ignored.</summary>
    public void RemoveTargets(IReadOnlyCollection<string> ids)
    {
        lock (_targetsGate)
        {
            Targets = Targets.Where(t => !ids.Contains(t.Id!)).ToList();
        }
    }

    /// <summary>
    /// Marks the rule deleted and runs <paramref name="remove"/> to drop it from its bus, unless it
    /// still has targets: EventBridge refuses to delete those with <c>ValidationException</c>.
    /// </summary>
    public void Delete(Action remove)
    {
        lock (_targetsGate)
        {
            if (Targets.Count > 0)
            {
                throw new InternalValidationException("Rule can't be deleted since it has targets.");
            }

            _deleted = true;
            remove();
        }
    }

    /// <summary>The error EventBridge answers for a rule that does not exist on a bus.</summary>
    public static InternalResourceNotFoundException NotFound(string? ruleName, string eventBusName) =>
        new($"Rule {ruleName} does not exist on EventBus {eventBusName}.");
}
