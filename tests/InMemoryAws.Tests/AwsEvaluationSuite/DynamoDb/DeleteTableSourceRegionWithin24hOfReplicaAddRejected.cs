// Translated from behavioral/dynamodb/check_delete_table_source_region_within_24h_of_replica_add_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.delete-table-source-region-within-24h-of-replica-add-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteTableSourceRegionWithin24hOfReplicaAddRejected
{
    private const string CheckId = "delete-table-source-region-within-24h-of-replica-add-rejected";

    /// <summary><c>(Error.Code, Error.Message)</c> <paramref name="fn"/> raises. An unexpected success raises too.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>
    /// DeleteTable on a global table's source region, right after that region sourced a new
    /// replica, is refused with a ValidationException naming the 24-hour restriction; the table
    /// stays ACTIVE. Removing the (non-source) replica region does not need the restriction to lift.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.delete-table-source-region-within-24h-of-replica-add-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var region1 = ctx.Region;
        var secondRegion = region1 != "us-west-2" ? "us-west-2" : "us-east-1";
        var ddb1 = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-gtdr-");
        await ddb1.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_AND_OLD_IMAGES },
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => DeleteTableIgnore(ddb1, tableName));
        await WaitTableActive(ddb1, tableName);
        await CreateReplicaWithRetry(ddb1, tableName, secondRegion);
        ctx.OnCleanup(() => RemoveReplicaThenWait(ddb1, tableName, secondRegion));
        await PollUntil(
            async () => (await ddb1.DescribeTableAsync(tableName)).Table,
            d => d.TableStatus == TableStatus.ACTIVE
                && (d.Replicas ?? []).Any(r => r.RegionName == secondRegion && r.ReplicaStatus == ReplicaStatus.ACTIVE),
            timeoutSeconds: 900,
            intervalSeconds: 15);

        var (code, message) = await ErrorCode(() => ddb1.DeleteTableAsync(tableName));
        Ensure(code == "ValidationException", $"expected ValidationException, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(
            message == "Replica cannot be deleted because it has acted as a source region for new replica(s) "
                + "being added to the table in the last 24 hours.",
            $"unexpected message for the refused delete: {Py.Repr(message)}");
        var afterRefusal = (await ddb1.DescribeTableAsync(tableName)).Table;
        Ensure(
            afterRefusal.TableStatus == TableStatus.ACTIVE,
            $"table status after the refused delete is {Py.Repr(afterRefusal.TableStatus?.Value)}, expected ACTIVE");

        // Positive control: once the table is no longer part of any replication group (the
        // destination replica, which never itself acted as a source, is removed), DeleteTable is an
        // ordinary table delete and is not subject to the source region's 24-hour restriction.
        await RemoveReplicaThenWait(ddb1, tableName, secondRegion);
        var noReplicas = (await ddb1.DescribeTableAsync(tableName)).Table;
        Ensure(
            noReplicas.Replicas is not { Count: > 0 },
            $"expected no replicas after removing {Py.Repr(secondRegion)}, got {Py.Repr(noReplicas.Replicas?.Select(r => r.RegionName).ToList())}");
        await ddb1.DeleteTableAsync(tableName);
        var deleted = await PollUntil(
            () => DescribeOrNone(ddb1, tableName),
            d => d is null || d.TableStatus == TableStatus.DELETING,
            timeoutSeconds: 60,
            intervalSeconds: 3);
        Ensure(
            deleted is null || deleted.TableStatus == TableStatus.DELETING,
            $"table after a delete with no replica attached is {Py.Repr(deleted?.TableStatus?.Value)}, expected gone or DELETING");
    }

    private static async Task<TableDescription?> DescribeOrNone(IAmazonDynamoDB ddb, string tableName)
    {
        var (response, error) = await FidelityContext.Capture(() => ddb.DescribeTableAsync(tableName));
        if (error is null)
        {
            return response!.Table;
        }

        if (error.Code is "ResourceNotFoundException" or "TableNotFoundException")
        {
            return null;
        }

        throw error.Exception;
    }

    private static Task WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 120) =>
        PollUntil(
            async () => (await ddb.DescribeTableAsync(tableName)).Table.TableStatus?.Value,
            s => s == "ACTIVE",
            timeoutSeconds,
            3);

    /// <summary>
    /// DeleteTable, where a table already gone or already being deleted counts as deleted; any
    /// other refusal, a lingering replica's included, raises.
    /// </summary>
    private static async Task DeleteTableIgnore(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is null)
        {
            return;
        }

        if (error.Code == "ResourceInUseException" && (error.Message ?? "").Contains("Table is being deleted", StringComparison.Ordinal))
        {
            return;
        }

        if (error.Code is not ("ResourceNotFoundException" or "TableNotFoundException"))
        {
            throw error.Exception;
        }
    }

    /// <summary>
    /// Remove the replica region and wait until DescribeTable lists none, or do nothing if the
    /// table is already gone or the replica is already removed (ReplicaUpdates Delete on a replica
    /// that is not there raises a ValidationException naming the missing replica, not a not-found
    /// code).
    /// </summary>
    private static async Task RemoveReplicaThenWait(IAmazonDynamoDB ddb1, string tableName, string secondRegion, double timeoutSeconds = 300.0)
    {
        var current = await DescribeOrNone(ddb1, tableName);
        if (current is null)
        {
            return;
        }

        if (!(current.Replicas ?? []).Any(r => r.RegionName == secondRegion))
        {
            return;
        }

        var error = await FidelityContext.CaptureError(() => ddb1.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = tableName,
            ReplicaUpdates = [new ReplicationGroupUpdate { Delete = new DeleteReplicationGroupMemberAction { RegionName = secondRegion } }],
        }));
        if (error is not null)
        {
            if (error.Code is not ("ResourceNotFoundException" or "TableNotFoundException"))
            {
                throw error.Exception;
            }

            return;
        }

        await PollUntil(
            () => DescribeOrNone(ddb1, tableName),
            d => d is null || (d.TableStatus == TableStatus.ACTIVE && d.Replicas is not { Count: > 0 }),
            timeoutSeconds,
            5);
    }

    /// <summary>
    /// UpdateTable's ReplicaUpdates "Create" issued right as the source table reaches ACTIVE can
    /// transiently fail with an AccessDeniedException wrapping an "UnrecognizedClientException"/
    /// "security token" message while cross-region replication setup settles -- retry for a bounded
    /// time rather than treating that as a hard failure.
    /// </summary>
    private static async Task CreateReplicaWithRetry(IAmazonDynamoDB ddb, string tableName, string region, double timeoutSeconds = 120, double intervalSeconds = 5)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.UpdateTableAsync(new UpdateTableRequest
            {
                TableName = tableName,
                ReplicaUpdates = [new ReplicationGroupUpdate { Create = new CreateReplicationGroupMemberAction { RegionName = region } }],
            }));
            if (error is null)
            {
                return;
            }

            var text = $"An error occurred ({error.Code}) when calling the UpdateTable operation: {error.Message}";
            var transient = error.Code == "AccessDeniedException" && text.ToLowerInvariant().Contains("security token", StringComparison.Ordinal);
            if (!transient || clock.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutSeconds, double intervalSeconds)
    {
        var clock = FidelityTime.StartNew();
        T last = default!;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"condition not met within {timeoutSeconds}s, last value: {Py.Repr(last)}");
    }
}
