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

    /// <summary>
    /// A queue or topic whose policy grants EventBridge nothing still receives EventBridge's
    /// deliveries, kept deliberately as for SNS: most test setups add no policy. A policy that
    /// denies EventBridge, or admits it only for other rules, is enforced.
    /// </summary>
    EventBridgeUngrantedTargets,

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
