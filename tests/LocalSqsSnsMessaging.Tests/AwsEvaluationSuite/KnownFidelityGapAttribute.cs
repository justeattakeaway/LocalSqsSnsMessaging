namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite;

/// <summary>
/// Root causes of the evaluation-suite checks LocalSqsSnsMessaging does not pass yet, as triaged in
/// the fidelity report. Each <see cref="KnownFidelityGapAttribute"/> names one.
/// </summary>
public enum FidelityGap
{
    /// <summary>No <c>x-amzn-query-error</c> header, so SDKs see <c>QueueDoesNotExist</c> rather than <c>AWS.SimpleQueueService.NonExistentQueue</c>.</summary>
    QueryErrorCodes,

    /// <summary><c>MD5OfMessageAttributes</c> is never computed.</summary>
    MessageAttributesMd5,

    /// <summary>Message move tasks never report a terminal status, and validation differs.</summary>
    MoveTasks,

    /// <summary>Batch request validation and per-entry failures.</summary>
    Batch,

    /// <summary>Input validation AWS performs that we skip.</summary>
    Validation,

    /// <summary><c>CreateQueue</c> on an existing name doesn't compare attributes or tags.</summary>
    IdempotentCreate,

    /// <summary>Receipt-handle lifecycle (idempotent deletes, stale and foreign handles).</summary>
    ReceiptHandles,

    /// <summary>FIFO semantics (dedup, ReceiveRequestAttemptId, sequence numbers, ...).</summary>
    Fifo,

    /// <summary>Tags passed to <c>CreateQueue</c> are dropped; tag validation differs.</summary>
    Tags,

    /// <summary><c>ListDeadLetterSourceQueues</c> never matches; redrive policy validation differs.</summary>
    DeadLetterQueues,

    /// <summary>The bus serves a single region.</summary>
    MultiRegion,

    /// <summary>The right failure, but a different error code or message.</summary>
    ErrorMessageText,

    /// <summary>Query protocol and raw HTTP handling.</summary>
    Protocol,

    /// <summary>A one-off behavioral difference.</summary>
    Other,
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
