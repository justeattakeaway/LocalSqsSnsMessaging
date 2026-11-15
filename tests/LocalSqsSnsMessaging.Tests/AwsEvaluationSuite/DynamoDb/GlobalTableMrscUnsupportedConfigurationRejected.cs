// Translated from behavioral/dynamodb/check_global_table_mrsc_unsupported_configuration_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.global-table-mrsc-unsupported-configuration-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GlobalTableMrscUnsupportedConfigurationRejected
{
    private const string CheckId = "global-table-mrsc-unsupported-configuration-rejected";

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
    private const string WrongReplicaCountMessage = "Unsupported replica count for global tables with MultiRegionConsistency set to STRONG.";
    private const string LsiUnsupportedMessage = "Local secondary indexes are not supported for global tables with MultiRegionConsistency set to STRONG.";

    /// <summary>
    /// UpdateTable refuses MultiRegionConsistency STRONG with only one additional replica Region,
    /// and on a table carrying a local secondary index, each with its documented message; the same
    /// table then accepts a STRONG request with one replica and one witness.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbGlobalTables, "UpdateTable's ReplicaUpdates and MultiRegionConsistency are not emulated: a STRONG request with one replica is refused \"At least one of ProvisionedThroughput, ... is required\", not with the replica-count message")]
    [DisplayName("dynamodb.global-table-mrsc-unsupported-configuration-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var (replicaRegion, witnessRegion) = MrscPeers(ctx.Region);

        var oneReplicaTable = FidelityContext.UniqueName("sim2real-behav-mrsc-count-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = oneReplicaTable,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => DeleteTableIfPresent(ddb, oneReplicaTable));
        await AwaitActive(ctx, ddb, oneReplicaTable);

        // (4a) MRSC must span exactly 3 Regions; one replica (2 Regions total) is refused outright,
        // before any cross-Region provisioning starts. Step (4c) is the accepted control on this table.
        var (code, message) = await ErrorCodeAndMessage(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = oneReplicaTable,
            MultiRegionConsistency = MultiRegionConsistency.STRONG,
            ReplicaUpdates = [new ReplicationGroupUpdate { Create = new CreateReplicationGroupMemberAction { RegionName = replicaRegion } }],
        }));
        Ensure(code == "ValidationException", $"expected ValidationException for a 1-replica STRONG request, got {code}");
        Ensure(message == WrongReplicaCountMessage, $"unexpected message {Py.Repr(message)}");
        var unchanged = (await ddb.DescribeTableAsync(oneReplicaTable)).Table;
        Ensure((unchanged.Replicas ?? []).Count == 0, $"expected no replica provisioned, got {Py.Repr(unchanged.Replicas?.Select(r => r.RegionName).ToList())}");
        Ensure(
            unchanged.MultiRegionConsistency is null,
            $"expected no MultiRegionConsistency set on the refused table, got {Py.Repr(unchanged.MultiRegionConsistency?.Value)}");

        // (4b) Local secondary indexes are incompatible with MRSC; a table carrying one is refused
        // before any replica or witness is created.
        var lsiTable = FidelityContext.UniqueName("sim2real-behav-mrsc-lsi-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = lsiTable,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("lsik", ScalarAttributeType.S),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = "lsi1",
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lsik", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => DeleteTableIfPresent(ddb, lsiTable));
        await AwaitActive(ctx, ddb, lsiTable);

        (code, message) = await ErrorCodeAndMessage(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = lsiTable,
            MultiRegionConsistency = MultiRegionConsistency.STRONG,
            ReplicaUpdates = [new ReplicationGroupUpdate { Create = new CreateReplicationGroupMemberAction { RegionName = replicaRegion } }],
            GlobalTableWitnessUpdates = [new GlobalTableWitnessGroupUpdate { Create = new CreateGlobalTableWitnessGroupMemberAction { RegionName = witnessRegion } }],
        }));
        Ensure(code == "ValidationException", $"expected ValidationException for an LSI-carrying STRONG request, got {code}");
        Ensure(message == LsiUnsupportedMessage, $"unexpected message {Py.Repr(message)}");
        var unchangedLsi = (await ddb.DescribeTableAsync(lsiTable)).Table;
        Ensure((unchangedLsi.Replicas ?? []).Count == 0, $"expected no replica provisioned, got {Py.Repr(unchangedLsi.Replicas?.Select(r => r.RegionName).ToList())}");
        Ensure(
            (unchangedLsi.GlobalTableWitnesses ?? []).Count == 0,
            $"expected no witness provisioned, got {Py.Repr(unchangedLsi.GlobalTableWitnesses?.Select(w => w.RegionName).ToList())}");

        // (4c) The empty, LSI-free table refused in (4a) accepts STRONG with one replica and one
        // witness, so the refusals above are the replica count and the LSI, not MRSC itself.
        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = oneReplicaTable,
            MultiRegionConsistency = MultiRegionConsistency.STRONG,
            ReplicaUpdates = [new ReplicationGroupUpdate { Create = new CreateReplicationGroupMemberAction { RegionName = replicaRegion } }],
            GlobalTableWitnessUpdates = [new GlobalTableWitnessGroupUpdate { Create = new CreateGlobalTableWitnessGroupMemberAction { RegionName = witnessRegion } }],
        });
        ctx.OnCleanup(() => TeardownGlobalTable(ddb, oneReplicaTable, replicaRegion, witnessRegion));
        await PollUntil(
            ctx,
            async () => (await ddb.DescribeTableAsync(oneReplicaTable)).Table,
            d => d.MultiRegionConsistency?.Value == "STRONG"
                && (d.Replicas ?? []).Select(r => r.RegionName).ToHashSet(StringComparer.Ordinal).SetEquals([replicaRegion])
                && (d.GlobalTableWitnesses ?? []).Select(w => w.RegionName).ToHashSet(StringComparer.Ordinal).SetEquals([witnessRegion]),
            capSeconds: ActiveWaitSeconds,
            intervalSeconds: 5.0,
            description: "DescribeTable lists the accepted replica and witness with MultiRegionConsistency STRONG");
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

    /// <summary>Wait until the table is ACTIVE.</summary>
    private static Task<string?> AwaitActive(FidelityContext ctx, IAmazonDynamoDB ddb, string name) =>
        PollUntil(
            ctx,
            async () => (await ddb.DescribeTableAsync(name)).Table.TableStatus?.Value,
            status => status == "ACTIVE",
            capSeconds: ActiveWaitSeconds,
            description: $"{name} ACTIVE");

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
