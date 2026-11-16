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

    /// <summary>
    /// A queue or topic whose policy grants EventBridge nothing still receives EventBridge's
    /// deliveries, kept deliberately as for SNS: most test setups add no policy. A policy that
    /// denies EventBridge, or admits it only for other rules, is enforced.
    /// </summary>
    EventBridgeUngrantedTargets,

    /// <summary>
    /// The check's own waits outlast the runner's 60-second budget for an emulator, so it fails
    /// against any emulator, however fast; against AWS it runs with a longer budget.
    /// </summary>
    EmulatorBudget,

    /// <summary>DynamoDB global tables (replicas in other regions) are not emulated.</summary>
    DynamoDbGlobalTables,

    /// <summary>A DynamoDB index keyed on more than one attribute per key role is refused.</summary>
    DynamoDbMultiAttributeKeys,

    /// <summary>DynamoDB provisioned throughput is reported, never enforced: nothing is throttled.</summary>
    DynamoDbThroughput,

    /// <summary>DynamoDB requests are not authenticated: SigV4 signatures and missing credentials are not checked.</summary>
    DynamoDbAuthentication,

    /// <summary>A one-off DynamoDB difference: a message's wording, the order of checks, an edge case.</summary>
    DynamoDbDetail,
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
