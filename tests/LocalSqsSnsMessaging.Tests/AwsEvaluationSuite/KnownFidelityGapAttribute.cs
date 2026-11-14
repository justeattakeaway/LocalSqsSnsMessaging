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

    /// <summary>SNS: the right failure, but a different error code or message.</summary>
    SnsErrorMessages,

    /// <summary>SNS: subscription lifecycle (ARN format, subscribing a missing queue, idempotent subscribe, unsubscribe).</summary>
    SnsSubscriptions,

    /// <summary>SNS: topic attributes (defaults, computed attributes, validation, immutability).</summary>
    SnsTopicAttributes,

    /// <summary>SNS: subscription attributes (read-back form, validation, unknown names).</summary>
    SnsSubscriptionAttributes,

    /// <summary>SNS: request validation AWS performs that we skip (names, publish parameters, message attributes, subjects).</summary>
    SnsValidation,

    /// <summary>SNS: PublishBatch validation and per-entry results.</summary>
    SnsBatch,

    /// <summary>SNS: FIFO topic semantics (sequence numbers, topic-level deduplication, throughput scope).</summary>
    SnsFifo,

    /// <summary>SNS: the JSON envelope delivered to SQS (fields, layout, signature, signing certificate).</summary>
    SnsEnvelope,

    /// <summary>SNS: raw message delivery (attribute conversion, limits, toggling).</summary>
    SnsRawDelivery,

    /// <summary>SNS: subscription filter policy evaluation and validation.</summary>
    SnsFilterPolicy,

    /// <summary>SNS: deliveries SQS refuses (queue policies, deleted queues) and subscription dead-letter queues.</summary>
    SnsDeliveryFailures,

    /// <summary>SNS: topic policies, AddPermission and RemovePermission.</summary>
    SnsPermissions,

    /// <summary>SNS: topic tags.</summary>
    SnsTags,

    /// <summary>SNS: list operations and their pagination tokens.</summary>
    SnsListing,

    /// <summary>SNS: <c>MessageStructure=json</c> publishes.</summary>
    SnsMessageStructure,

    /// <summary>SNS: FIFO topic message archiving and replay.</summary>
    SnsArchive,

    /// <summary>SNS topics and subscriptions are not regional yet (planned as separate work).</summary>
    SnsRegions,
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
