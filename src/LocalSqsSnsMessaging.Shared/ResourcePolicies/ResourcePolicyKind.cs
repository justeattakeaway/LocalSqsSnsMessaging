namespace LocalSqsSnsMessaging;

/// <summary>
/// What differs between the resource policies of SQS queues, SNS topics and EventBridge event
/// buses: the prefix their actions are named with, the document an edit starts from when the
/// resource has none, and how the service stores and compares a document.
/// </summary>
internal sealed class ResourcePolicyKind
{
    /// <summary>An SQS queue's <c>Policy</c> attribute.</summary>
    public static readonly ResourcePolicyKind Queue = new("sqs", "2012-10-17", static arn => $"{arn}/SQSDefaultPolicy")
    {
        StoresAccountPrincipalsAsRootArns = true,
        ComparesWithoutStatementIds = true,
    };

    /// <summary>An SNS topic's <c>Policy</c> attribute.</summary>
    public static readonly ResourcePolicyKind Topic = new("sns", "2008-10-17", static _ => "__default_policy_ID");

    /// <summary>An event bus's policy (<c>PutPermission</c>, <c>DescribeEventBus</c>).</summary>
    public static readonly ResourcePolicyKind EventBus = new("events", "2012-10-17", static _ => null);

    private readonly Func<string, string?> _documentId;

    private ResourcePolicyKind(string actionPrefix, string version, Func<string, string?> documentId)
    {
        ActionPrefix = actionPrefix;
        Version = version;
        _documentId = documentId;
    }

    /// <summary>The service prefix statements name actions with: <c>sqs</c>, <c>sns</c> or <c>events</c>.</summary>
    public string ActionPrefix { get; }

    /// <summary>The <c>Version</c> of a document the service writes from scratch.</summary>
    public string Version { get; }

    /// <summary>
    /// Whether the service stores an <c>AWS</c> principal given as a bare account id as that
    /// account's root ARN (SQS does; SNS and EventBridge keep what they are given).
    /// </summary>
    public bool StoresAccountPrincipalsAsRootArns { get; private init; }

    /// <summary>
    /// Whether two documents that differ only in their statements' <c>Sid</c>s are the same policy
    /// when <c>CreateQueue</c> compares a requested policy with an existing queue's (SQS's rule).
    /// </summary>
    public bool ComparesWithoutStatementIds { get; private init; }

    /// <summary>The <c>Id</c> of a document the service writes from scratch for <paramref name="resourceArn"/>, if it gives one.</summary>
    public string? DocumentId(string resourceArn) => _documentId(resourceArn);

    public override string ToString() => ActionPrefix;
}
