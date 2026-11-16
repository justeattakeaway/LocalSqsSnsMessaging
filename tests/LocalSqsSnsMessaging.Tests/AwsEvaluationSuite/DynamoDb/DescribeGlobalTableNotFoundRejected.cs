// Translated from behavioral/dynamodb/check_describe_global_table_not_found_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.describe-global-table-not-found-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DescribeGlobalTableNotFoundRejected
{
    /// <summary>
    /// DescribeGlobalTable (version 2017.11.29) raises GlobalTableNotFoundException with the exact message for a
    /// never-created name and for an ACTIVE version 2019.11.21 global table with two replica regions, which
    /// ListGlobalTables does not list either.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.describe-global-table-not-found-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var region1 = ctx.Region;
        // A global table replica must be in a genuinely different AWS region than
        // ctx.region, so this pair is pinned rather than derived.
        var region2 = region1 != "us-west-2" ? "us-west-2" : "us-east-1";
        var tableName = FidelityContext.UniqueName("sim2real-behav-gt-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_AND_OLD_IMAGES },
        });
        ctx.OnCleanup(() => DeleteTableWithRetry(ddb, tableName));
        await ddb.WaitForTableExists(tableName, delaySeconds: 2, maxAttempts: 250);

        // Promote it to a genuine (2019.11.21-native) global table via UpdateTable's
        // ReplicaUpdates, since CreateGlobalTable itself is rejected outright here.
        await CreateReplicaWithRetry(ddb, tableName, region2);
        ctx.OnCleanup(() => Swallow(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = tableName,
            ReplicaUpdates = [new ReplicationGroupUpdate { Delete = new DeleteReplicationGroupMemberAction { RegionName = region2 } }],
        })));
        var describedTable = await PollUntil(
            async () => (await ddb.DescribeTableAsync(tableName)).Table,
            table => table.TableStatus == TableStatus.ACTIVE
                && (table.Replicas ?? []).Any(r => r.RegionName == region2 && r.ReplicaStatus == ReplicaStatus.ACTIVE),
            timeoutSeconds: 280,
            intervalSeconds: 10);
        var regionsPresent = (describedTable.Replicas ?? []).Select(r => r.RegionName).Append(region1).ToHashSet(StringComparer.Ordinal);
        Ensure(regionsPresent.SetEquals([region1, region2]), Py.Repr(regionsPresent.ToList()));

        // The refusal is the contract: DescribeGlobalTable (legacy) does not recognize
        // this real, ACTIVE, cross-region-replicated global table either — the legacy
        // control plane has no path to ever "see" a table promoted the native way.
        var (realCode, realMessage) = await Error(() => ddb.DescribeGlobalTableAsync(new DescribeGlobalTableRequest { GlobalTableName = tableName }));
        Ensure(realCode == "GlobalTableNotFoundException", $"expected GlobalTableNotFoundException for a real, active global table, got {realCode}");
        var expectedReal = $"Global table not found: Global table with name: '{tableName}' does not exist.";
        Ensure(realMessage == expectedReal, $"expected message {Py.Repr(expectedReal)}, got {Py.Repr(realMessage)}");

        // Effect absent via an independent read: ListGlobalTables (the legacy
        // enumeration) does not surface this real, native global table either.
        var listedNames = ((await ddb.ListGlobalTablesAsync(new ListGlobalTablesRequest())).GlobalTables ?? []).Select(gt => gt.GlobalTableName).ToHashSet(StringComparer.Ordinal);
        Ensure(!listedNames.Contains(tableName), Py.Repr(listedNames.ToList()));

        var fakeName = FidelityContext.UniqueName("sim2real-noglobaltable-");
        var (fakeCode, fakeMessage) = await Error(() => ddb.DescribeGlobalTableAsync(new DescribeGlobalTableRequest { GlobalTableName = fakeName }));
        Ensure(fakeCode == "GlobalTableNotFoundException", $"expected GlobalTableNotFoundException, got {fakeCode}");
        var expectedFake = $"Global table not found: Global table with name: '{fakeName}' does not exist.";
        Ensure(fakeMessage == expectedFake, $"expected message {Py.Repr(expectedFake)}, got {Py.Repr(fakeMessage)}");
    }

    /// <summary>(code, message) of the ClientError fn raises; an unexpected success raises AssertionError.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a rejection, but the call SUCCEEDED and returned {Py.Repr(result)}");
        }

        return (error.Code, error.Message);
    }

    private static async Task Swallow(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null && error.Code is not ("ResourceNotFoundException" or "TableNotFoundException"))
        {
            throw error.Exception;
        }
    }

    /// <summary>
    /// DeleteTable refuses a table that still lists a replica (ValidationException) and can refuse
    /// with ResourceInUseException right after a replica delete settles, so wait for the replica
    /// list to empty, then retry the delete rather than issuing one attempt and leaking the table.
    /// </summary>
    private static async Task DeleteTableWithRetry(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 50, double intervalSeconds = 5)
    {
        var replicaClock = FidelityTime.StartNew();
        while (true)
        {
            var (described, error) = await FidelityContext.Capture(() => ddb.DescribeTableAsync(tableName));
            if (error is not null)
            {
                if (error.Code == "ResourceNotFoundException")
                {
                    return;
                }

                throw error.Exception;
            }

            var replicas = described!.Table.Replicas;
            if (replicas is not { Count: > 0 } || replicaClock.Elapsed >= TimeSpan.FromSeconds(900))
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(10));
        }

        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
            if (error is null)
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }
    }

    /// <summary>
    /// UpdateTable's ReplicaUpdates "Create" issued right as the source table reaches ACTIVE can
    /// transiently fail with an AccessDeniedException wrapping an "UnrecognizedClientException"/
    /// "security token" message while DynamoDB's cross-region replication setup settles — retry for
    /// a bounded time rather than treating that as a hard failure.
    /// </summary>
    private static async Task CreateReplicaWithRetry(IAmazonDynamoDB ddb, string tableName, string region, double timeoutSeconds = 90, double intervalSeconds = 5)
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
