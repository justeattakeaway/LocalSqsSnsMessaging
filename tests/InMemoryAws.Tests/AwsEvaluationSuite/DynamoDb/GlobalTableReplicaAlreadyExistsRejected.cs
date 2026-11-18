// Translated from behavioral/dynamodb/check_global_table_replica_already_exists_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.global-table-replica-already-exists-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GlobalTableReplicaAlreadyExistsRejected
{
    private const string CheckId = "global-table-replica-already-exists-rejected";

    /// <summary>
    /// UpdateTable ReplicaUpdates Create for a region that is already a replica of the global table raises
    /// ValidationException with the exact message and leaves the replica set unchanged.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.global-table-replica-already-exists-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var region1 = ctx.Region;
        var region2 = SecondaryRegion(region1);
        var globalName = FidelityContext.UniqueName("sim2real-behav-gtr-");

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
            async () => (await ddb.DescribeTableAsync(globalName)).Table.TableStatus?.Value,
            status => status == "ACTIVE",
            timeoutSeconds: 60,
            intervalSeconds: 2);

        // Accounts/regions that must use global tables version 2019.11.21 reject the legacy
        // CreateGlobalTable call outright ("DynamoDB global tables version 2019.11.21 must be
        // used to create new global tables"); establish the global table the 2019.11.21-native
        // way instead, via UpdateTable's ReplicaUpdates from the original region's client (no
        // second-region client required — DynamoDB creates the replica table remotely).
        //
        // A ReplicaUpdates "Create" issued the instant the source table (with a freshly-enabled
        // stream) reaches ACTIVE can be transiently rejected with an AccessDeniedException wrapping
        // an "UnrecognizedClientException"/"security token" message while DynamoDB's cross-region
        // replication setup settles — retry instead of treating that as a hard failure.
        await CreateReplicaWithRetry(ddb, globalName, region2);
        ctx.OnCleanup(() => SafeRemoveReplica(ddb, globalName, region2));

        // IMPORTANT: legacy DescribeGlobalTable/UpdateGlobalTable (2017.11.29-only) do NOT recognize a
        // table made global via UpdateTable's ReplicaUpdates — they 404 with
        // GlobalTableNotFoundException PERMANENTLY for such a table. The 2019.11.21-native way to
        // observe AND mutate replica state is DescribeTable (Table.Replicas[].ReplicaStatus) /
        // UpdateTable (ReplicaUpdates) — used below.
        var described = await PollUntil(
            async () => (await ddb.DescribeTableAsync(globalName)).Table,
            table => table.TableStatus == TableStatus.ACTIVE
                && (table.Replicas ?? []).Any(r => r.RegionName == region2 && r.ReplicaStatus == ReplicaStatus.ACTIVE),
            timeoutSeconds: 280,
            intervalSeconds: 10);
        var regionsPresent = (described.Replicas ?? []).Select(r => r.RegionName).Append(region1).ToHashSet(StringComparer.Ordinal);
        Ensure(regionsPresent.SetEquals([region1, region2]), Py.Repr((regionsPresent.ToList(), new List<string> { region1, region2 })));

        var (code, message) = await ErrorCodeAndMessage(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = globalName,
            ReplicaUpdates = [new ReplicationGroupUpdate { Create = new CreateReplicationGroupMemberAction { RegionName = region2 } }],
        }));
        // Real AWS rejects re-"Create"-ing an ALREADY-present replica region with a generic
        // ValidationException (not the more specific ReplicaAlreadyExistsException, which DynamoDB
        // reserves for the legacy 2017.11.29 CreateGlobalTable/UpdateGlobalTable surface) — assert the
        // code AND its exact message, so this verifies the rejection is about the duplicate replica
        // and not some unrelated validation failure.
        Ensure(code == "ValidationException", Py.Repr((code, message)));
        // AWS's own wording, including "a the" and the typographic quotes around the name.
        var expectedMessage =
            $"Failed to create a the new replica of table with name: ‘{globalName}’ "
            + "because one or more replicas already existed as tables.";
        Ensure(message == expectedMessage, $"expected message {Py.Repr(expectedMessage)}, got {Py.Repr(message)}");

        // The replication group must remain exactly as it was before the rejected update.
        var unchanged = (await ddb.DescribeTableAsync(globalName)).Table;
        var unchangedRegions = (unchanged.Replicas ?? []).Select(r => r.RegionName).Append(region1).ToHashSet(StringComparer.Ordinal);
        Ensure(unchangedRegions.SetEquals([region1, region2]), Py.Repr((unchangedRegions.ToList(), new List<string> { region1, region2 })));
    }

    /// <summary>A region distinct from the primary, for a global table replica.</summary>
    private static string SecondaryRegion(string primaryRegion) => primaryRegion != "us-west-2" ? "us-west-2" : "us-east-1";

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

    private static async Task SafeRemoveReplica(IAmazonDynamoDB ddb, string globalName, string region)
    {
        var error = await FidelityContext.CaptureError(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = globalName,
            ReplicaUpdates = [new ReplicationGroupUpdate { Delete = new DeleteReplicationGroupMemberAction { RegionName = region } }],
        }));
        if (error is not null)
        {
            if (error.Code is not ("ResourceNotFoundException" or "TableNotFoundException"))
            {
                throw error.Exception;
            }

            return;
        }

        try
        {
            await PollUntil(
                async () => ((await ddb.DescribeTableAsync(globalName)).Table.Replicas ?? []).Select(r => r.RegionName).ToHashSet(StringComparer.Ordinal),
                regions => !regions.Contains(region),
                timeoutSeconds: 120,
                intervalSeconds: 10);
        }
        catch (Amazon.Runtime.AmazonServiceException ex) when (ex.ErrorCode is "ResourceNotFoundException" or "TableNotFoundException")
        {
        }
    }

    private static async Task<(string? Code, string? Message)> ErrorCodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("real DynamoDB rejects re-adding a replica region that is already present, but the UpdateTable call SUCCEEDED");
        }

        return (error.Code, error.Message);
    }

    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code is not ("ResourceNotFoundException" or "TableNotFoundException"))
        {
            throw error.Exception;
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
