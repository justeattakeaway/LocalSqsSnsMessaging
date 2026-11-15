// Translated from behavioral/dynamodb/check_create_global_table_already_exists_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.create-global-table-already-exists-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateGlobalTableAlreadyExistsRejected
{
    /// <summary>
    /// On a table made global through UpdateTable ReplicaUpdates, the 2017.11.29 CreateGlobalTable fails
    /// with ValidationException and AWS's exact global-tables-version message and leaves both replica
    /// regions in place.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbGlobalTables, "UpdateTable with only ReplicaUpdates is refused as a ValidationException (no update), so the table never becomes global")]
    [DisplayName("dynamodb.create-global-table-already-exists-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var region1 = ctx.Region;
        var region2 = SecondaryRegion(region1);
        var tableName = FidelityContext.UniqueName("sim2real-globaltable-");

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = new StreamSpecification
            {
                StreamEnabled = true,
                StreamViewType = StreamViewType.NEW_AND_OLD_IMAGES,
            },
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, tableName));
        await PollUntil(
            async () => (await ddb.DescribeTableAsync(tableName)).Table.TableStatus?.Value,
            status => status == "ACTIVE",
            timeoutSeconds: 60,
            intervalSeconds: 2);

        // Make the table a genuine (2019.11.21-native) global table first, since accounts that
        // reject CreateGlobalTable outright never let it succeed even once.
        await CreateReplicaWithRetry(ddb, tableName, region2);
        ctx.OnCleanup(() => SafeRemoveReplica(ddb, tableName, region2));
        var described = await PollUntil(
            async () => (await ddb.DescribeTableAsync(tableName)).Table,
            table => table.TableStatus == "ACTIVE"
                && (table.Replicas ?? []).Any(r => r.RegionName == region2 && r.ReplicaStatus == "ACTIVE"),
            timeoutSeconds: 280,
            intervalSeconds: 10);
        var regionsPresent = (described.Replicas ?? []).Select(r => r.RegionName).Append(region1).ToHashSet(StringComparer.Ordinal);
        Ensure(regionsPresent.SetEquals([region1, region2]), Py.Repr((SetRepr(regionsPresent), SetRepr([region1, region2]))));

        Task CreateAgain() => ddb.CreateGlobalTableAsync(new CreateGlobalTableRequest
        {
            GlobalTableName = tableName,
            ReplicationGroup = [new Replica { RegionName = region1 }, new Replica { RegionName = region2 }],
        });

        var (code, message) = await ErrorCodeAndMessage(CreateAgain);
        Ensure(code == "ValidationException", Py.Repr((code, message)));
        const string expectedMsg =
            "One or more parameter values were invalid: DynamoDB global tables version 2017.11.29 "
            + "is not supported. We recommend using DynamoDB global tables version 2019.11.21, instead "
            + "of version 2017.11.29 (Legacy).";
        Ensure(message == expectedMsg, $"expected message {Py.Repr(expectedMsg)}, got {Py.Repr(message)}");

        // The rejected CreateGlobalTable must be a pure no-op: an independent DescribeTable
        // (not the failed call's own response) confirms the replication group is exactly what
        // it was before the rejected attempt -- neither region dropped nor duplicated.
        var unchanged = (await ddb.DescribeTableAsync(tableName)).Table;
        var unchangedRegions = (unchanged.Replicas ?? []).Select(r => r.RegionName).Append(region1).ToHashSet(StringComparer.Ordinal);
        Ensure(unchangedRegions.SetEquals([region1, region2]), Py.Repr((SetRepr(unchangedRegions), SetRepr([region1, region2]))));
    }

    /// <summary>A region distinct from the primary, for a global table replica.</summary>
    private static string SecondaryRegion(string primaryRegion) => primaryRegion != "us-west-2" ? "us-west-2" : "us-east-1";

    /// <summary>Python's repr of a set of strings (sorted here, for a stable message).</summary>
    private static SetText SetRepr(IEnumerable<string> values) =>
        new("{" + string.Join(", ", values.Order(StringComparer.Ordinal).Select(v => Py.Repr(v))) + "}");

    private static async Task<(string? Code, string Message)> ErrorCodeAndMessage(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null)
        {
            return (error.Code, error.Message ?? string.Empty);
        }

        throw Fail("expected a service error, but the call succeeded");
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
            if (!transient || clock.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }
    }

    private static async Task SafeRemoveReplica(IAmazonDynamoDB ddb, string tableName, string region)
    {
        var error = await FidelityContext.CaptureError(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = tableName,
            ReplicaUpdates = [new ReplicationGroupUpdate { Delete = new DeleteReplicationGroupMemberAction { RegionName = region } }],
        }));
        if (error is not null && error.Code is not ("ResourceNotFoundException" or "TableNotFoundException"))
        {
            throw error.Exception;
        }
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
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
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

    /// <summary>Text that <see cref="Py.Repr"/> prints as is (a set's repr).</summary>
    private sealed record SetText(string Text)
    {
        public override string ToString() => Text;
    }
}
