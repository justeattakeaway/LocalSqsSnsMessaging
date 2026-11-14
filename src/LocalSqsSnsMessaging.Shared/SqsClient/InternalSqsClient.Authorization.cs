using System.Net;

namespace LocalSqsSnsMessaging;

internal sealed partial class InternalSqsClient
{
    /// <summary>The AWS-managed SQS key, which every account has.</summary>
    internal const string AwsManagedSqsKeyAlias = "alias/aws/sqs";

    /// <summary>
    /// Who is making the request, when it came over HTTP; <see langword="null"/> for the raw
    /// client, which is never subject to a queue's policy.
    /// </summary>
    internal SqsCaller? Caller { get; init; }

    /// <summary>
    /// Refuses the request when the queue's policy explicitly denies <paramref name="action"/> to
    /// the caller, or when an anonymous caller would need the queue's KMS key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only explicit Deny statements are enforced, so a queue with no policy, or one that only
    /// allows, serves every caller as before.
    /// </para>
    /// <para>
    /// An unsigned request is served as the account (it is what <c>CreateSqsClient()</c> sends),
    /// except where the queue's policy grants the action to everyone (<c>"Principal": "*"</c>) and
    /// the request meets that grant's conditions: such a request is the anonymous access the grant
    /// exists for. Anonymous callers cannot use a KMS key, so SQS refuses them on an SSE-KMS queue.
    /// </para>
    /// </remarks>
    private void Authorize(SqsQueueResource queue, string action)
    {
        if (Caller is null
            || queue.Attributes?.TryGetValue(InternalQueueAttributeName.Policy, out var policy) != true
            || string.IsNullOrEmpty(policy))
        {
            return;
        }

        var account = Caller.AccountId(_bus);
        var asAccount = new SqsQueuePolicy.RequestContext(action, queue.Arn, account, Caller.Arn(_bus), Caller.SourceIp, Caller.SecureTransport);
        var anonymous = !Caller.IsSigned
            && SqsQueuePolicy.GrantsToEveryone(policy, asAccount with { PrincipalAccount = null, PrincipalArn = null });
        var request = anonymous ? asAccount with { PrincipalAccount = null, PrincipalArn = null } : asAccount;

        if (SqsQueuePolicy.DeniesExplicitly(policy, request))
        {
#pragma warning disable CA1308 // SQS names the action in lower case.
            throw AccessDenied(
                $"User: {(anonymous ? "anonymous" : request.PrincipalArn)} is not authorized to perform: " +
                $"sqs:{action.ToLowerInvariant()} on resource: {queue.Arn} with an explicit deny in a resource-based policy");
#pragma warning restore CA1308
        }

        if (anonymous
            && action is SqsActionName.SendMessage or SqsActionName.ReceiveMessage
            && KmsMasterKeyId(queue) is not null)
        {
            throw AccessDenied(
                "Access to the resource The specified queue does not exist or you do not have access to it. is denied.");
        }
    }

    private static AwsServiceException AccessDenied(string message) => new(message)
    {
        ErrorCode = "AccessDenied",
        StatusCode = HttpStatusCode.Forbidden,
    };

    /// <summary>The queue's <c>KmsMasterKeyId</c>, or <see langword="null"/> when it is not SSE-KMS.</summary>
    private static string? KmsMasterKeyId(SqsQueueResource queue) =>
        queue.Attributes?.TryGetValue(InternalQueueAttributeName.KmsMasterKeyId, out var keyId) == true && !string.IsNullOrEmpty(keyId)
            ? keyId
            : null;

    /// <summary>
    /// Fails a send the way SQS does when the queue's KMS key does not exist: SQS resolves the key
    /// only when it encrypts, so <c>SetQueueAttributes</c> accepts any key and the send is refused.
    /// </summary>
    /// <remarks>
    /// The library models no KMS. A key id or key ARN is taken to exist, as are the AWS-managed
    /// aliases (<c>alias/aws/...</c>, including <see cref="AwsManagedSqsKeyAlias"/>); any other alias
    /// exists only when it is listed in <see cref="InMemoryAwsBus.KmsKeyAliases"/>.
    /// </remarks>
    private void EnsureKmsKeyExists(SqsQueueResource queue)
    {
        if (KmsMasterKeyId(queue) is not { } keyId)
        {
            return;
        }

        string alias;
        if (keyId.StartsWith("alias/", StringComparison.Ordinal))
        {
            alias = keyId;
        }
        else if (keyId.StartsWith("arn:", StringComparison.Ordinal) && keyId.IndexOf(":alias/", StringComparison.Ordinal) is var at and >= 0)
        {
            alias = keyId[(at + 1)..];
        }
        else
        {
            return;
        }

        if (alias.StartsWith("alias/aws/", StringComparison.Ordinal)
            || _bus.KmsKeyAliases.Contains(alias)
            || _bus.KmsKeyAliases.Contains(alias["alias/".Length..]))
        {
            return;
        }

        var aliasArn = keyId.StartsWith("arn:", StringComparison.Ordinal)
            ? keyId
            : $"arn:aws:kms:{queue.Region}:{queue.AccountId}:{keyId}";
        throw new AwsServiceException(
            $"Alias {aliasArn} is not found. (Service: AWSKMS; Status Code: 400; Error Code: NotFoundException; " +
            $"Request ID: {Guid.NewGuid()}; Proxy: null)")
        {
            ErrorCode = "KmsNotFound",
            StatusCode = HttpStatusCode.BadRequest,
        };
    }
}
