using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

/// <summary>
/// In-memory representation of an EventBridge rule and its targets.
/// </summary>
internal sealed class RuleResource
{
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

    /// <summary>Targets attached to this rule, in insertion order, keyed by target id.</summary>
    public List<Target> Targets { get; } = [];
}
