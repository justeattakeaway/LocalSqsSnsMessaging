// Translated from behavioral/dynamodb/check_global_table_create_describe_update_list.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.global-table-create-describe-update-list</summary>
[Category("AwsEvaluationSuite")]
public sealed class GlobalTableCreateDescribeUpdateList
{
    private const string CheckId = "global-table-create-describe-update-list";

    /// <summary>
    /// A new table has no replicas and is listed; adding a replica region with UpdateTable ReplicaUpdates makes
    /// DescribeTable report both regions and ListTables in the second region list the table. UpdateTable replicas are
    /// the current (version 2019.11.21) global table mechanism; CreateGlobalTable serves only version 2017.11.29.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbGlobalTables, "UpdateTable's ReplicaUpdates are not emulated: it is refused with \"At least one of ProvisionedThroughput, BillingMode, UpdateStreamEnabled, GlobalSecondaryIndexUpdates or SSESpecification ...\"")]
    [DisplayName("dynamodb.global-table-create-describe-update-list")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var primaryRegion = ctx.Region;
        // Concrete, real AWS region names, not a stand-in for a genuinely global or region-pinned
        // resource: a global-table replica MUST live in some second real region, and there is no way
        // to derive "some other region" from ctx alone.
        var secondRegion = primaryRegion != "us-west-2" ? "us-west-2" : "us-east-1";

        var ddb1 = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-gt-");

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

        // Before joining a global table, the table has no replicas and is a
        // perfectly normal (single-region) table.
        var described = (await ddb1.DescribeTableAsync(tableName)).Table;
        Ensure((described.Replicas ?? []).Count == 0, $"expected no replicas yet, got {Py.Repr(described.Replicas?.Select(r => r.RegionName).ToList())}");

        var listed = await TableNames(ddb1);
        Ensure(listed.Contains(tableName), $"expected {tableName} in ListTables, got absence");

        var ddb2 = ctx.DynamoClient(region: secondRegion);

        await CreateReplicaWithRetry(ddb1, tableName, secondRegion);
        ctx.OnCleanup(() => RemoveReplicaThenDelete(ddb1, tableName, secondRegion));

        var describedAfter = await PollUntil(
            async () => (await ddb1.DescribeTableAsync(tableName)).Table,
            d => (d.Replicas ?? []).Count == 1 && d.Replicas!.All(r => r.ReplicaStatus == ReplicaStatus.ACTIVE),
            timeoutSeconds: 900,
            intervalSeconds: 15);
        var regions = describedAfter.Replicas.Select(r => r.RegionName).ToHashSet(StringComparer.Ordinal);
        Ensure(regions.SetEquals([secondRegion]), $"expected replica set {{{Py.Repr(secondRegion)}}}, got {Py.Repr(regions.ToList())}");

        var listedAfter = await TableNames(ddb2);
        Ensure(listedAfter.Contains(tableName), $"expected auto-provisioned replica {tableName} in ListTables for {secondRegion}");
    }

    /// <summary>Every table name in the client's region, read through all ListTables pages.</summary>
    private static async Task<List<string>> TableNames(IAmazonDynamoDB ddb)
    {
        var names = new List<string>();
        var request = new ListTablesRequest();
        while (true)
        {
            var page = await ddb.ListTablesAsync(request);
            names.AddRange(page.TableNames ?? []);
            if (page.LastEvaluatedTableName is null)
            {
                return names;
            }

            request.ExclusiveStartTableName = page.LastEvaluatedTableName;
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

    private static async Task RemoveReplicaThenDelete(IAmazonDynamoDB ddb1, string tableName, string secondRegion)
    {
        // Removing a replica region via UpdateTable also tears down the
        // underlying table AWS auto-provisioned in that region, so only the
        // primary-region table needs an explicit DeleteTable afterwards.
        var updateError = await FidelityContext.CaptureError(() => ddb1.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = tableName,
            ReplicaUpdates = [new ReplicationGroupUpdate { Delete = new DeleteReplicationGroupMemberAction { RegionName = secondRegion } }],
        }));
        if (updateError is null)
        {
            var clock = FidelityTime.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(300))
            {
                var (desc, error) = await FidelityContext.Capture(() => ddb1.DescribeTableAsync(tableName));
                if (error is not null)
                {
                    if (error.Code is not ("ResourceNotFoundException" or "TableNotFoundException"))
                    {
                        throw error.Exception;
                    }

                    break;
                }

                if ((desc!.Table.Replicas ?? []).All(r => r.RegionName != secondRegion))
                {
                    break;
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(5));
            }
        }
        else if (updateError.Code is not ("ResourceNotFoundException" or "TableNotFoundException"))
        {
            throw updateError.Exception;
        }

        await DeleteTableIgnore(ddb1, tableName);
    }

    /// <summary>
    /// UpdateTable's ReplicaUpdates "Create" issued right as the source table reaches ACTIVE can
    /// transiently fail with an AccessDeniedException wrapping an "UnrecognizedClientException"/
    /// "security token" message while DynamoDB's cross-region replication setup settles -- retry for a
    /// bounded time rather than treating that as a hard failure. A DIFFERENT AccessDeniedException, one
    /// that names the AWSServiceRoleForDynamoDBReplication service-linked role directly, means the
    /// account's own permission boundary denies that role the access it needs, and a global-table
    /// replica genuinely cannot be provisioned under that restriction -- that one is not transient and
    /// becomes the check's own assertion.
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

            var message = error.Message ?? string.Empty;
            if (error.Code == "AccessDeniedException" && message.Contains("AWSServiceRoleForDynamoDBReplication", StringComparison.Ordinal))
            {
                throw new FidelityAssertionException(
                    $"account denies the AWSServiceRoleForDynamoDBReplication service-linked role the permissions it needs to provision a global-table replica: {error}",
                    error.Exception);
            }

            var transient = error.Code == "AccessDeniedException" && message.Contains("security token", StringComparison.OrdinalIgnoreCase);
            if (!transient || clock.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }
    }
}
