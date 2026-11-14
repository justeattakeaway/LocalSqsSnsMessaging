namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite;

/// <summary>
/// Root causes of the evaluation-suite checks LocalSqsSnsMessaging does not pass yet, as triaged in
/// the fidelity report. Each <see cref="KnownFidelityGapAttribute"/> names one.
/// </summary>
public enum FidelityGap
{
    /// <summary>Validation AWS performs that is deliberately not enforced (the purge and re-create cooldowns).</summary>
    Validation,

    /// <summary>A one-off behavioral difference, kept for compatibility.</summary>
    Other,

    /// <summary>SNS topics and subscriptions are not regional yet (planned as separate work).</summary>
    SnsRegions,

    /// <summary>EventBridge refuses with a different error code or message text than AWS.</summary>
    EventBridgeErrorMessages,

    /// <summary>EventBridge request validation AWS performs (names, limits, shapes) is missing.</summary>
    EventBridgeValidation,

    /// <summary>Event bus lifecycle: create, describe, update, delete and their read surfaces.</summary>
    EventBridgeEventBuses,

    /// <summary>Rule lifecycle: PutRule, DescribeRule, DeleteRule, enable and disable, schedule expressions.</summary>
    EventBridgeRules,

    /// <summary>Target lifecycle and validation: PutTargets, RemoveTargets, ListTargetsByRule.</summary>
    EventBridgeTargets,

    /// <summary>List operations: pagination, prefixes and tokens.</summary>
    EventBridgeListing,

    /// <summary>Tags on event buses and rules.</summary>
    EventBridgeTags,

    /// <summary>Event bus resource policies: PutPermission, RemovePermission and the bus Policy.</summary>
    EventBridgePermissions,

    /// <summary>PutEvents: entry validation, partial failures, request limits.</summary>
    EventBridgePutEvents,

    /// <summary>The delivered event envelope: its fields, their values and their form.</summary>
    EventBridgeEnvelope,

    /// <summary>The event pattern language: validation and matching (TestEventPattern and delivery).</summary>
    EventBridgeEventPatterns,

    /// <summary>Target input: Input, InputPath and InputTransformer.</summary>
    EventBridgeInput,

    /// <summary>Delivery failures: dead-letter queues, retry policies and resource policies on targets.</summary>
    EventBridgeDeliveryFailures,

    /// <summary>SQS FIFO targets: MessageGroupId and ordering.</summary>
    EventBridgeFifoTargets,

    /// <summary>SNS topic targets.</summary>
    EventBridgeSnsTargets,

    /// <summary>Event bus targets: forwarding events from one bus to another.</summary>
    EventBridgeBusTargets,

    /// <summary>Archives and replays.</summary>
    EventBridgeArchives,

    /// <summary>Scheduled rules firing on their schedule.</summary>
    EventBridgeSchedules,

    /// <summary>EventBridge is not regional yet (planned as separate work, with SNS regions).</summary>
    EventBridgeRegions,

    /// <summary>
    /// The check's own waits outlast the runner's 60-second budget for an emulator, so it fails
    /// against any emulator, however fast; against AWS it runs with a longer budget.
    /// </summary>
    EmulatorBudget,
}

/// <summary>
/// Marks an evaluation-suite check that LocalSqsSnsMessaging does not pass yet, so it is skipped and
/// the suite stays green. Set <c>LOCALSQSSNS_RUN_KNOWN_GAPS=1</c> to run these tests anyway: a test
/// that then passes means its gap has been fixed and the attribute can be removed.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class KnownFidelityGapAttribute : SkipAttribute
{
    public const string RunKnownGapsVariable = "LOCALSQSSNS_RUN_KNOWN_GAPS";

    public KnownFidelityGapAttribute(FidelityGap gap, string detail)
        : base($"Known fidelity gap ({gap}): {detail}")
    {
        Gap = gap;
        Detail = detail;
    }

    public FidelityGap Gap { get; }

    public string Detail { get; }

    public override Task<bool> ShouldSkip(TestRegisteredContext context)
        => Task.FromResult(Environment.GetEnvironmentVariable(RunKnownGapsVariable) is not ("1" or "true"));
}
