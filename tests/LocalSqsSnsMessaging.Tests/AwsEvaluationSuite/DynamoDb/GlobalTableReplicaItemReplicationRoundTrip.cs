// Translated from behavioral/dynamodb/check_global_table_replica_item_replication_round_trip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.global-table-replica-item-replication-round-trip</summary>
[Category("AwsEvaluationSuite")]
public sealed class GlobalTableReplicaItemReplicationRoundTrip
{
    private const string CheckId = "global-table-replica-item-replication-round-trip";

    /// <summary>
    /// A global table replicates item writes between its regions: a put, an update and a delete
    /// issued in either region read back identically, or disappear, in the other. Each region's
    /// DescribeTable lists the other as an ACTIVE replica throughout.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.global-table-replica-item-replication-round-trip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var region1 = ctx.Region;
        var secondRegion = region1 != "us-west-2" ? "us-west-2" : "us-east-1";
        var ddb1 = ctx.Dynamo;
        var ddb2 = ctx.DynamoClient(region: secondRegion);

        var tableName = FidelityContext.UniqueName("sim2real-behav-gtir-");

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

        var described1 = await PollUntil(
            async () => (await ddb1.DescribeTableAsync(tableName)).Table,
            d => d.TableStatus == TableStatus.ACTIVE
                && (d.Replicas ?? []).Any(r => r.RegionName == secondRegion && r.ReplicaStatus == ReplicaStatus.ACTIVE),
            timeoutSeconds: 900,
            intervalSeconds: 15);
        var regions1 = described1.Replicas.Select(r => r.RegionName).ToHashSet(StringComparer.Ordinal);
        Ensure(
            regions1.SetEquals([secondRegion]),
            $"DescribeTable in {Py.Repr(region1)} expected replica set {{{Py.Repr(secondRegion)}}}, got {Py.Repr(regions1.ToList())}");

        var described2 = await PollUntil(
            async () => (await ddb2.DescribeTableAsync(tableName)).Table,
            d => d.TableStatus == TableStatus.ACTIVE
                && (d.Replicas ?? []).Any(r => r.RegionName == region1 && r.ReplicaStatus == ReplicaStatus.ACTIVE),
            timeoutSeconds: 300,
            intervalSeconds: 10);
        var regions2 = described2.Replicas.Select(r => r.RegionName).ToHashSet(StringComparer.Ordinal);
        Ensure(
            regions2.SetEquals([region1]),
            $"DescribeTable in {Py.Repr(secondRegion)} expected replica set {{{Py.Repr(region1)}}}, got {Py.Repr(regions2.ToList())}");

        // (1) put in region1 -> read in region2, identical attributes.
        var pk = FidelityContext.UniqueName("sim2real-item-");
        var item = Item(("pk", S(pk)), ("attr_s", S("replicated-value")), ("attr_n", N("42")), ("attr_bool", Bool(true)));
        await ddb1.PutItemAsync(tableName, item);
        var key = Item(("pk", S(pk)));
        var replicated = await PollForItem(ddb2, tableName, key, timeoutSeconds: 120, intervalSeconds: 2);
        Ensure(Py.Equal(replicated, item), $"item replicated to {Py.Repr(secondRegion)} is {Py.Repr(replicated)}, expected {Py.Repr(item)}");

        // (2) update in region2 -> read back in region1.
        await ddb2.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = tableName,
            Key = key,
            UpdateExpression = "SET attr_s = :v",
            ExpressionAttributeValues = Item((":v", S("updated-in-second-region"))),
        });
        var expectedAfterUpdate = new Dictionary<string, AttributeValue>(item, StringComparer.Ordinal) { ["attr_s"] = S("updated-in-second-region") };
        var updatedBack = await PollUntil(
            async () => (await ddb1.GetItemAsync(new GetItemRequest { TableName = tableName, Key = key })).Item,
            i => Py.Equal(i, expectedAfterUpdate),
            timeoutSeconds: 120,
            intervalSeconds: 2);
        Ensure(
            Py.Equal(updatedBack, expectedAfterUpdate),
            $"item read back in {Py.Repr(region1)} is {Py.Repr(updatedBack)}, expected {Py.Repr(expectedAfterUpdate)}");

        // (3) delete in region1 -> absence propagates to region2.
        await ddb1.DeleteItemAsync(tableName, key);
        await PollUntil(
            async () => (await ddb2.GetItemAsync(new GetItemRequest { TableName = tableName, Key = key })).Item,
            i => i is null,
            timeoutSeconds: 120,
            intervalSeconds: 2);
        var stillThere = (await ddb1.GetItemAsync(new GetItemRequest { TableName = tableName, Key = key, ConsistentRead = true })).Item;
        Ensure(stillThere is null, $"item still present in {Py.Repr(region1)} after delete: {Py.Repr(stillThere)}");
    }

    /// <summary>
    /// Poll GetItem (no ConsistentRead: cross-region reads are inherently eventually
    /// consistent) until the item appears, returning its attribute map.
    /// </summary>
    private static async Task<Dictionary<string, AttributeValue>> PollForItem(IAmazonDynamoDB ddb, string tableName, Dictionary<string, AttributeValue> key, double timeoutSeconds, double intervalSeconds) =>
        (await PollUntil(
            async () => (await ddb.GetItemAsync(new GetItemRequest { TableName = tableName, Key = key })).Item,
            i => i is not null,
            timeoutSeconds: timeoutSeconds,
            intervalSeconds: intervalSeconds))!;

    private static Task<string?> WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 120) =>
        PollUntil(
            async () => (await ddb.DescribeTableAsync(tableName)).Table.TableStatus?.Value,
            s => s == "ACTIVE",
            timeoutSeconds: timeoutSeconds,
            intervalSeconds: 3);

    /// <summary>DeleteTable, where a table already gone or already being deleted counts as deleted.</summary>
    private static async Task DeleteTableIgnore(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is null)
        {
            return;
        }

        if (error.Code == "ResourceInUseException" && (error.Message ?? string.Empty).Contains("Table is being deleted", StringComparison.Ordinal))
        {
            return;
        }

        if (error.Code is not ("ResourceNotFoundException" or "TableNotFoundException"))
        {
            throw error.Exception;
        }
    }

    /// <summary>
    /// Remove the replica region and wait until DescribeTable lists none: DeleteTable refuses a
    /// table that still has a replica.
    /// </summary>
    private static async Task RemoveReplicaThenWait(IAmazonDynamoDB ddb1, string tableName, string secondRegion, double timeoutSeconds = 300.0)
    {
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
            async () => (await ddb1.DescribeTableAsync(tableName)).Table,
            d => d.TableStatus == TableStatus.ACTIVE && d.Replicas is not { Count: > 0 },
            timeoutSeconds: timeoutSeconds,
            intervalSeconds: 5);
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

            var transient = error.Code == "AccessDeniedException"
                && (error.Message ?? string.Empty).Contains("security token", StringComparison.OrdinalIgnoreCase);
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
        T? last = default;
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
