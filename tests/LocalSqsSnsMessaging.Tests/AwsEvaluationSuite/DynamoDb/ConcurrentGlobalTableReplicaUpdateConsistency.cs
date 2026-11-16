// Translated from behavioral/dynamodb/check_concurrent_global_table_replica_update_consistency.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.concurrent-global-table-replica-update-consistency</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentGlobalTableReplicaUpdateConsistency
{
    private const string Success = "SUCCESS";

    /// <summary>
    /// Concurrent UpdateTable ReplicaUpdates that each add the same region to one global table leave
    /// DescribeTable's Replicas listing that region exactly once: never duplicated, never lost.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.concurrent-global-table-replica-update-consistency")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var region1 = ctx.Region;
        var region2 = SecondaryRegion(region1);
        var tableName = FidelityContext.UniqueName("sim2real-behav-gtc-");

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_AND_OLD_IMAGES },
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, tableName));
        await PollUntil(
            async () => (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table.TableStatus.Value,
            status => status == "ACTIVE",
            timeoutS: 60,
            intervalS: 2);
        ctx.OnCleanup(() => SafeRemoveReplica(ddb, tableName, region2));
        var busy = ("ResourceInUseException", "The resource which you are attempting to change is in use.");
        var exists = ("ValidationException", $"Global table with name: '{tableName}' already exists.");

        const int concurrency = 4;
        var barrier = new AsyncBarrier(concurrency);
        var outcomes = new object[concurrency];

        async Task RaceAdd(int i)
        {
            await barrier.SignalAndWait(cancellationToken);
            outcomes[i] = await AttemptCreateReplica(ddb, tableName, region2);
        }

        using (ctx.Concurrently())
        {
            await Task.WhenAll(Enumerable.Range(0, concurrency).Select(i => Task.Run(() => RaceAdd(i), cancellationToken)));
        }

        // Every non-transient outcome must be either the accepted create or a genuine "someone else
        // already added/is adding this region" conflict -- never an unrelated error. On the
        // 2019.11.21-native UpdateTable(ReplicaUpdates) surface (unlike the legacy
        // CreateGlobalTable/UpdateGlobalTable trio), real AWS names a losing racer that arrives AFTER
        // another's Create has already taken effect with a generic ValidationException, not the more
        // specific ReplicaAlreadyExistsException. Require the message to actually name the conflict.
        var codes = outcomes.Select(o => o is string s ? s : (((string Code, string Message))o).Code).ToList();
        var allowed = new HashSet<string>(StringComparer.Ordinal) { Success, "ResourceInUseException", "ValidationException" };
        Ensure(codes.All(allowed.Contains), Py.Repr(outcomes));
        foreach (var o in outcomes)
        {
            if (o is ValueTuple<string, string> refusal)
            {
                var expected = refusal.Item1 == busy.Item1 ? busy : exists;
                Ensure(refusal == expected, $"losing racer with an unrelated refusal: {Py.Repr(refusal)}");
            }
        }

        Ensure(codes.Contains(Success), $"no racer's Create was ever accepted: {Py.Repr(outcomes)}");

        var described = await PollUntil(
            async () => (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table,
            table => table.TableStatus == TableStatus.ACTIVE
                && (table.Replicas ?? []).Any(r => r.RegionName == region2 && r.ReplicaStatus == ReplicaStatus.ACTIVE),
            timeoutS: 280,
            intervalS: 10);
        var replicaRegions = (described.Replicas ?? []).Select(r => r.RegionName).ToList();
        // No duplicate entries (a corrupted merge of the racing Creates could double-list the
        // region), and no lost entry.
        var matching = replicaRegions.Where(r => r == region2).ToList();
        Ensure(matching.Count == 1, $"expected region {Py.Repr(region2)} exactly once, got {Py.Repr(replicaRegions)}");
    }

    /// <summary>A region distinct from the primary, for a global table replica.</summary>
    private static string SecondaryRegion(string primaryRegion) => primaryRegion != "us-west-2" ? "us-west-2" : "us-east-1";

    /// <summary>
    /// One racer's attempt to add <c>region</c> as a replica. A brand-new table's replication setup
    /// can transiently surface an AccessDeniedException wrapping an "UnrecognizedClientException"/
    /// "security token" message while unrelated infra settles -- that is NOT a race outcome, so
    /// retry it briefly. Every OTHER outcome (success, or a genuine "region already exists / table
    /// busy" conflict from a co-racer) is returned as-is, immediately -- that IS the race outcome
    /// under test. A failure is returned as <c>(code, message)</c> so the caller can check the message
    /// names the actual conflict, not just the code.
    /// </summary>
    private static async Task<object> AttemptCreateReplica(IAmazonDynamoDB ddb, string tableName, string region, double timeoutS = 30, double intervalS = 3)
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
                return Success;
            }

            var code = error.Code ?? "UNKNOWN_ERROR";
            var transient = code == "AccessDeniedException" && (error.Message ?? "").Contains("security token", StringComparison.OrdinalIgnoreCase);
            if (!transient || clock.Elapsed.TotalSeconds >= timeoutS)
            {
                return (code, error.Message ?? error.Exception.ToString());
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }
    }

    /// <summary>
    /// Remove the replica and wait until DescribeTable lists none: DeleteTable refuses a table that
    /// still has a replica it acted as the source for.
    /// </summary>
    private static async Task SafeRemoveReplica(IAmazonDynamoDB ddb, string tableName, string region, double timeoutS = 240.0)
    {
        var error = await FidelityContext.CaptureError(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = tableName,
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

        await PollUntil(
            async () => (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName })).Table,
            table => table.TableStatus == TableStatus.ACTIVE && table.Replicas is not { Count: > 0 },
            timeoutS: timeoutS,
            intervalS: 2);
    }

    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
        if (error is not null && error.Code is not ("ResourceNotFoundException" or "TableNotFoundException"))
        {
            throw error.Exception;
        }
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, double intervalS)
    {
        var clock = FidelityTime.StartNew();
        T? last = default;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"condition not met within {timeoutS.ToString(CultureInfo.InvariantCulture)}s, last value: {Py.Repr(last)}");
    }

    /// <summary><c>threading.Barrier</c>: every participant waits until all have arrived.</summary>
    private sealed class AsyncBarrier(int participants)
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining = participants;

        public Task SignalAndWait(CancellationToken cancellationToken)
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
            {
                _all.TrySetResult();
            }

            return _all.Task.WaitAsync(cancellationToken);
        }
    }
}
