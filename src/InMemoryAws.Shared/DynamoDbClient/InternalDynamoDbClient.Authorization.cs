namespace InMemoryAws.DynamoDb;

/// <summary>Enforcing a table's resource policy on the requests that act on the table.</summary>
internal sealed partial class InternalDynamoDbClient
{
    /// <summary>
    /// Refuses the request with <c>AccessDeniedException</c> when the table's resource policy
    /// explicitly denies <c>dynamodb:</c><paramref name="action"/> on <paramref name="resourceArn"/>
    /// (the table's own ARN unless given, e.g. an index's) to the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called once the table has been found, so a missing table is refused as missing before any
    /// policy is read, as SQS orders a queue's lookup and its policy. The policy that applies is
    /// always the table's, whichever of its resources the request acts on.
    /// </para>
    /// <para>
    /// As for SQS queues and EventBridge buses (<see cref="ResourcePolicy"/>), only explicit Deny
    /// statements are enforced: a table with no policy, or one that only allows, serves every
    /// request as before. The library does not see who sends a DynamoDB request, so every request
    /// acts for the cloud's account (its root user), over TLS and with no <c>aws:SourceIp</c>, as
    /// EventBridge's PutEvents does. No DynamoDB condition keys (<c>dynamodb:LeadingKeys</c> and the
    /// like) are passed, so a statement conditioned on one is skipped.
    /// </para>
    /// </remarks>
    private void Authorize(DynamoTable table, string action, string? resourceArn = null)
    {
        if (ResourcePolicy.Parse(ResourcePolicyKind.Table, table.ResourcePolicy.Policy) is not { } policy)
        {
            return;
        }

        var resource = resourceArn ?? table.Arn;
        var callerArn = $"arn:aws:iam::{_cloud.CurrentAccountId}:root";
        var request = new ResourcePolicy.RequestContext(action, resource, _cloud.CurrentAccountId, callerArn, SourceIp: null);
        if (policy.DeniesExplicitly(request))
        {
            throw DynamoDbErrors.AccessDenied(
                $"User: {callerArn} is not authorized to perform: {ResourcePolicyKind.Table.ActionPrefix}:{action} on resource: {resource} " +
                "with an explicit deny in a resource-based policy");
        }
    }

    /// <summary>
    /// The ARN a request naming <paramref name="indexName"/> acts on, whether or not the table has
    /// that index; <see langword="null"/> (the table's own ARN) when it names none.
    /// </summary>
    private static string? IndexArn(DynamoTable table, string? indexName) =>
        indexName is null ? null : $"{table.Arn}/index/{indexName}";
}
