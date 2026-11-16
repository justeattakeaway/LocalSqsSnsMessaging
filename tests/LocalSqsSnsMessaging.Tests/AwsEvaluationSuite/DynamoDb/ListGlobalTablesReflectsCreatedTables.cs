// Translated from behavioral/dynamodb/check_list_global_tables_reflects_created_tables.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.list-global-tables-reflects-created-tables</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListGlobalTablesReflectsCreatedTables
{
    private const string CheckId = "list-global-tables-reflects-created-tables";

    /// <summary>
    /// ListGlobalTables lists only 2017.11.29 global tables: a table made global via UpdateTable
    /// ReplicaUpdates (version 2019.11.21) is absent before and after DescribeTable shows its replica
    /// in another region ACTIVE.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.list-global-tables-reflects-created-tables")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var region1 = ctx.Region;
        var region2 = SecondaryRegion(region1);
        var globalName = FidelityContext.UniqueName("sim2real-behav-lgt-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = globalName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_AND_OLD_IMAGES },
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, globalName));
        await PollUntil(
            async () => (await ddb.DescribeTableAsync(globalName)).Table.TableStatus.Value,
            status => status == "ACTIVE",
            timeoutSeconds: 60,
            intervalSeconds: 2);

        var before = await ListGlobalTableNames(ddb);
        Ensure(
            !before.Contains(globalName),
            $"expected {globalName} absent from ListGlobalTables before any replica exists, got it present in {Py.Repr(before)}");

        // Some accounts reject CreateGlobalTable (2017.11.29); UpdateTable ReplicaUpdates works
        // everywhere and DynamoDB creates the remote replica itself.
        await CreateReplicaWithRetry(ddb, globalName, region2);
        ctx.OnCleanup(() => SafeRemoveReplica(ddb, globalName, region2));

        // ListGlobalTables never shows this table, so wait on DescribeTable's Replicas instead.
        var described = await PollUntil(
            async () => (await ddb.DescribeTableAsync(globalName)).Table,
            table => table.TableStatus == TableStatus.ACTIVE
                && (table.Replicas ?? []).Any(r => r.RegionName == region2 && r.ReplicaStatus == ReplicaStatus.ACTIVE),
            timeoutSeconds: 280,
            intervalSeconds: 10);
        var replicas = described.Replicas ?? [];
        Ensure(
            replicas.Any(r => r.RegionName == region2),
            $"expected an ACTIVE replica in {region2} via DescribeTable, got replicas {Py.Repr(replicas.Select(r => new Dictionary<string, object?> { ["RegionName"] = r.RegionName, ["ReplicaStatus"] = r.ReplicaStatus }).ToList())}");

        var after = await ListGlobalTableNames(ddb);
        Ensure(
            !after.Contains(globalName),
            $"expected {globalName} absent from ListGlobalTables even with a genuine ACTIVE replica in {region2}, got it present in {Py.Repr(after)}");
    }

    /// <summary>A region distinct from the primary, for a global table replica.</summary>
    private static string SecondaryRegion(string primaryRegion) => primaryRegion != "us-west-2" ? "us-west-2" : "us-east-1";

    /// <summary>
    /// Add a replica, retrying the transient "security token" AccessDeniedException that a Create
    /// issued right as the source table reaches ACTIVE can raise while cross-region setup settles.
    /// </summary>
    private static async Task CreateReplicaWithRetry(IAmazonDynamoDB ddb, string tableName, string region, double timeoutSeconds = 90, double intervalSeconds = 5)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            try
            {
                await ddb.UpdateTableAsync(new UpdateTableRequest
                {
                    TableName = tableName,
                    ReplicaUpdates = [new ReplicationGroupUpdate { Create = new CreateReplicationGroupMemberAction { RegionName = region } }],
                });
                return;
            }
            catch (AmazonServiceException ex) when (
                ex.ErrorCode == "AccessDeniedException"
                && ex.Message.Contains("security token", StringComparison.OrdinalIgnoreCase)
                && clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
            }
        }
    }

    private static async Task SafeRemoveReplica(IAmazonDynamoDB ddb, string globalName, string region)
    {
        try
        {
            await ddb.UpdateTableAsync(new UpdateTableRequest
            {
                TableName = globalName,
                ReplicaUpdates = [new ReplicationGroupUpdate { Delete = new DeleteReplicationGroupMemberAction { RegionName = region } }],
            });
        }
        catch (ResourceNotFoundException)
        {
        }
    }

    private static async Task<List<string>> ListGlobalTableNames(IAmazonDynamoDB ddb)
    {
        var names = new List<string>();
        var request = new ListGlobalTablesRequest();
        while (true)
        {
            var resp = await ddb.ListGlobalTablesAsync(request);
            names.AddRange((resp.GlobalTables ?? []).Select(g => g.GlobalTableName));
            var last = resp.LastEvaluatedGlobalTableName;
            if (string.IsNullOrEmpty(last))
            {
                return names;
            }

            request.ExclusiveStartGlobalTableName = last;
        }
    }

    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        try
        {
            await ddb.DeleteTableAsync(tableName);
        }
        catch (ResourceNotFoundException)
        {
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
