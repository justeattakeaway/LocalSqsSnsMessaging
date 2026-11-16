// Translated from behavioral/dynamodb/check_global_table_mrsc_strong_consistency_and_conditional_write_conflict.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.global-table-mrsc-strong-consistency-and-conditional-write-conflict</summary>
[Category("AwsEvaluationSuite")]
public sealed class GlobalTableMrscStrongConsistencyAndConditionalWriteConflict
{
    private const string CheckId = "global-table-mrsc-strong-consistency-and-conditional-write-conflict";

    /// <summary>
    /// The Regions AWS documents for MRSC global tables. Any three of them may form one table, so the
    /// replica and witness are the first two of this list other than ctx.region.
    /// </summary>
    private static readonly string[] MrscRegions =
    [
        "us-east-1", "us-east-2", "us-west-2", "eu-west-1", "eu-central-1", "ap-northeast-1",
        "ap-northeast-2", "ap-southeast-1", "ca-central-1", "eu-west-2", "eu-west-3", "eu-north-1",
        "eu-south-2", "ap-northeast-3", "ap-south-1",
    ];

    private const double ActiveWaitSeconds = 900.0;
    private const string TtlRejectedMessage = "Time To Live is not supported for global tables with MultiRegionConsistency set to STRONG.";

    /// <summary>
    /// An MRSC global table reports MultiRegionConsistency STRONG from every full-replica Region,
    /// a strongly consistent read in one Region sees a write that just completed in another, and a
    /// stale conditional write from a different Region fails against the cross-Region-latest version.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.global-table-mrsc-strong-consistency-and-conditional-write-conflict")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var primaryRegion = ctx.Region;
        var (replicaRegion, witnessRegion) = MrscPeers(primaryRegion);
        var ddb1 = ctx.Dynamo;
        var ddb2 = ctx.DynamoClient(region: replicaRegion);

        var table = FidelityContext.UniqueName("sim2real-behav-mrsc-");
        await ddb1.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => DeleteTableIfPresent(ddb1, table));
        await PollUntil(
            ctx,
            async () => (await ddb1.DescribeTableAsync(table)).Table.TableStatus?.Value,
            status => status == "ACTIVE",
            capSeconds: ActiveWaitSeconds,
            description: $"{table} ACTIVE");

        await ddb1.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            MultiRegionConsistency = MultiRegionConsistency.STRONG,
            ReplicaUpdates = [new ReplicationGroupUpdate { Create = new CreateReplicationGroupMemberAction { RegionName = replicaRegion } }],
            GlobalTableWitnessUpdates = [new GlobalTableWitnessGroupUpdate { Create = new CreateGlobalTableWitnessGroupMemberAction { RegionName = witnessRegion } }],
        });
        ctx.OnCleanup(() => TeardownGlobalTable(ddb1, table, replicaRegion, witnessRegion));

        var described = await PollUntil(
            ctx,
            async () => (await ddb1.DescribeTableAsync(table)).Table,
            d => d.TableStatus == TableStatus.ACTIVE
                && (d.Replicas ?? []).Any(r => r.RegionName == replicaRegion && r.ReplicaStatus == ReplicaStatus.ACTIVE)
                && (d.GlobalTableWitnesses ?? []).Any(w => w.RegionName == witnessRegion && w.WitnessStatus?.Value == "ACTIVE"),
            capSeconds: ActiveWaitSeconds,
            intervalSeconds: 10.0,
            description: "MRSC replica and witness ACTIVE via DescribeTable");

        // (1) MultiRegionConsistency is reported as STRONG from the primary Region, and the replica
        // Region's own DescribeTable call -- not just the Region that issued the UpdateTable -- agrees.
        // A witness holds no table of its own in its Region, so it has no DescribeTable to call there.
        Ensure(
            described.MultiRegionConsistency?.Value == "STRONG",
            $"expected MultiRegionConsistency STRONG on {primaryRegion}, got {Py.Repr(described.MultiRegionConsistency?.Value)}");
        var describedFromReplica = (await ddb2.DescribeTableAsync(table)).Table;
        Ensure(
            describedFromReplica.MultiRegionConsistency?.Value == "STRONG",
            $"expected MultiRegionConsistency STRONG from {replicaRegion}, got {Py.Repr(describedFromReplica.MultiRegionConsistency?.Value)}");
        var replicaRegions = described.Replicas.Select(r => r.RegionName).ToHashSet(StringComparer.Ordinal);
        Ensure(replicaRegions.SetEquals([replicaRegion]), $"expected replica Region {{{replicaRegion}}}, got {Py.Repr(replicaRegions.ToList())}");
        var witnessRegions = described.GlobalTableWitnesses.Select(w => w.RegionName).ToHashSet(StringComparer.Ordinal);
        Ensure(witnessRegions.SetEquals([witnessRegion]), $"expected witness Region {{{witnessRegion}}}, got {Py.Repr(witnessRegions.ToList())}");

        // (2) A write that completes in the primary Region is visible to a strongly consistent read
        // issued against the replica Region immediately afterward -- no propagation wait.
        await ddb1.PutItemAsync(table, Item(("pk", S("item-a")), ("v", S("written-in-primary"))));
        var crossRegionRead = await ddb2.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("item-a"))), ConsistentRead = true });
        Ensure(
            Py.Equal(crossRegionRead.Item, Item(("pk", S("item-a")), ("v", S("written-in-primary")))),
            $"expected the primary-Region write visible via a strongly consistent read in {replicaRegion}, got {Py.Repr(crossRegionRead.Item)}");

        // (3) A conditional write succeeds once in the primary Region; a second conditional write from
        // the replica Region, carrying the now-stale precondition, is evaluated against that same
        // cross-Region-latest version and fails -- it is not racing a locally stale copy.
        await ddb1.PutItemAsync(table, Item(("pk", S("item-b")), ("ctr", N("0"))));
        await ddb1.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("item-b"))),
            UpdateExpression = "SET ctr = :new",
            ConditionExpression = "ctr = :old",
            ExpressionAttributeValues = Item((":new", N("1")), (":old", N("0"))),
        });
        var (code, message) = await ErrorCodeAndMessage(() => ddb2.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("item-b"))),
            UpdateExpression = "SET ctr = :new",
            ConditionExpression = "ctr = :old",
            ExpressionAttributeValues = Item((":new", N("2")), (":old", N("0"))),
        }));
        Ensure(code == "ConditionalCheckFailedException", $"expected ConditionalCheckFailedException, got {code} ({message})");
        Ensure(message == "The conditional request failed", $"unexpected message {Py.Repr(message)}");
        var winner = (await ddb1.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("item-b"))), ConsistentRead = true })).Item;
        Ensure(Py.Equal(winner, Item(("pk", S("item-b")), ("ctr", N("1")))), $"expected the primary Region's write (ctr=1) to stand, got {Py.Repr(winner)}");
        var winnerFromReplica = (await ddb2.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S("item-b"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(winnerFromReplica, winner),
            $"expected the same winning value from {replicaRegion}, got {Py.Repr(winnerFromReplica)} vs {Py.Repr(winner)}");

        // (4) TTL is a documented unsupported setting for MRSC and is refused with its exact message.
        (code, message) = await ErrorCodeAndMessage(() => ddb1.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
        {
            TableName = table,
            TimeToLiveSpecification = new TimeToLiveSpecification { Enabled = true, AttributeName = "ttl" },
        }));
        Ensure(code == "ValidationException", $"expected ValidationException enabling TTL on an MRSC table, got {code}");
        Ensure(message == TtlRejectedMessage, $"unexpected message {Py.Repr(message)}");
    }

    /// <summary>The replica and witness Regions for an MRSC table whose base table is in region.</summary>
    private static (string Replica, string Witness) MrscPeers(string region)
    {
        Ensure(MrscRegions.Contains(region), $"MRSC global tables are not available in {Py.Repr(region)}; documented Regions: {Py.Repr(new Tuple<string[]>(MrscRegions))}");
        var peers = MrscRegions.Where(r => r != region).Take(2).ToList();
        return (peers[0], peers[1]);
    }

    /// <summary>
    /// A poll's timeout: capSeconds, bounded by the runner's remaining time less 5 s so the poll fails
    /// with its own message.
    /// </summary>
    private static double BudgetSeconds(FidelityContext ctx, double capSeconds) => Math.Max(1.0, Math.Min(capSeconds, ctx.TimeLeftSeconds - 5.0));

    /// <summary>True when the runner's remaining time, not the wait's own deadline, ended a capped wait.</summary>
    private static bool BudgetSpent(FidelityContext ctx) => ctx.TimeLeftSeconds <= 6.0;

    /// <summary>The (Error.Code, Error.Message) fn() raises; an unexpected success fails loudly.</summary>
    private static async Task<(string? Code, string Message)> ErrorCodeAndMessage<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, but the call succeeded and returned {Py.Repr(result)}");
        }

        return (error.Code, error.Message ?? string.Empty);
    }

    /// <summary>Poll probe() until predicate(result), under a deadline capped by the run budget.</summary>
    private static async Task<T> PollUntil<T>(FidelityContext ctx, Func<Task<T>> probe, Func<T, bool> predicate, double capSeconds, double intervalSeconds = 2.0, string description = "condition")
    {
        var timeoutSeconds = BudgetSeconds(ctx, capSeconds);
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

        if (BudgetSpent(ctx))
        {
            throw new TimeoutException($"{description}: the run budget ended first; not graded");
        }

        throw Fail($"{description} not met within {timeoutSeconds.ToString("F0", CultureInfo.InvariantCulture)}s. Last result: {Py.Repr(last)}");
    }

    private static async Task DeleteTableIfPresent(IAmazonDynamoDB ddb, string name)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(name));
        if (error is null || error.Code == "ResourceNotFoundException")
        {
            return;
        }

        if (error.Code == "ResourceInUseException" && (error.Message ?? string.Empty).Contains("being deleted", StringComparison.Ordinal))
        {
            return;
        }

        throw error.Exception;
    }

    /// <summary>Remove the replica and witness, wait until both are gone, then delete the base table.</summary>
    private static async Task TeardownGlobalTable(IAmazonDynamoDB ddb, string name, string replicaRegion, string witnessRegion)
    {
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(600.0);
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.UpdateTableAsync(new UpdateTableRequest
            {
                TableName = name,
                ReplicaUpdates = [new ReplicationGroupUpdate { Delete = new DeleteReplicationGroupMemberAction { RegionName = replicaRegion } }],
                GlobalTableWitnessUpdates = [new GlobalTableWitnessGroupUpdate { Delete = new DeleteGlobalTableWitnessGroupMemberAction { RegionName = witnessRegion } }],
            }));
            if (error is null)
            {
                break;
            }

            if (error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= deadline)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(5.0));
        }

        while (true)
        {
            var (described, error) = await FidelityContext.Capture(() => ddb.DescribeTableAsync(name));
            if (error is not null)
            {
                if (error.Code == "ResourceNotFoundException")
                {
                    return;
                }

                throw error.Exception;
            }

            var table = described!.Table;
            if (table.Replicas is not { Count: > 0 } && table.GlobalTableWitnesses is not { Count: > 0 })
            {
                await DeleteTableIfPresent(ddb, name);
                return;
            }

            if (clock.Elapsed >= deadline)
            {
                throw Fail(
                    $"replica/witness of {name} still present after teardown wait: replicas={Py.Repr(table.Replicas?.Select(r => r.RegionName).ToList())} witnesses={Py.Repr(table.GlobalTableWitnesses?.Select(w => w.RegionName).ToList())}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(5.0));
        }
    }
}
