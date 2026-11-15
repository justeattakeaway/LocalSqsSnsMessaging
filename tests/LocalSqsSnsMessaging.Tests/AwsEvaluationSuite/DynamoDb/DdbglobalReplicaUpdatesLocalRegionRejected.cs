// Translated from behavioral/dynamodb/check_ddbglobal_replica_updates_local_region_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ddbglobal-replica-updates-local-region-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DdbglobalReplicaUpdatesLocalRegionRejected
{
    private const string CheckId = "ddbglobal-replica-updates-local-region-rejected";
    private const string ErrValidation = "ValidationException";
    private const string LocalRegionMessage = "Cannot add or delete the local region through ReplicaUpdates. Use CreateTable, DeleteTable, or UpdateTable as required.";

    /// <summary>
    /// ReplicaUpdates Create and Delete naming the table's own region are refused with
    /// ValidationException, the refused Delete leaves the table and its item intact, and a legal
    /// UpdateTable on the same table then succeeds.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.DynamoDbGlobalTables, "UpdateTable doesn't take ReplicaUpdates: naming the local region is answered as an UpdateTable with no supported update, not AWS's local-region message")]
    [DisplayName("dynamodb.ddbglobal-replica-updates-local-region-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbglobal-local-");

        // No stream: the local-region refusal precedes global-table preconditions, and enabling the
        // stream later is the positive control.
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => DeleteTableIgnore(ddb, table));
        await WaitTableActive(ddb, table);
        var item = Item(("pk", S("survivor")), ("payload", S("must-outlive-both-refusals")), ("n", N("3")));
        await ddb.PutItemAsync(table, item);
        var before = await GetItem(ddb, table, "survivor");
        Ensure(Py.Equal(before, item), $"read back {Py.Repr(before)} immediately after the write of {Py.Repr(item)}");

        var (code, message) = await Refusal(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            ReplicaUpdates = [new ReplicationGroupUpdate { Create = new CreateReplicationGroupMemberAction { RegionName = ctx.Region } }],
        }));
        Ensure(
            code == ErrValidation,
            $"ReplicaUpdates Create naming the table's own region {Py.Repr(ctx.Region)} returned {Py.Repr(code)}, "
            + $"expected {Py.Repr(ErrValidation)}; message was {Py.Repr(message)}");
        Ensure(message == LocalRegionMessage, $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        Ensure(
            message.Contains("local region", StringComparison.Ordinal),
            $"the Create form was refused, but not for the local-region reason: {Py.Repr(message)}");

        (code, message) = await Refusal(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            ReplicaUpdates = [new ReplicationGroupUpdate { Delete = new DeleteReplicationGroupMemberAction { RegionName = ctx.Region } }],
        }));
        Ensure(
            code == ErrValidation,
            $"ReplicaUpdates Delete naming the table's own region {Py.Repr(ctx.Region)} returned {Py.Repr(code)}, "
            + $"expected {Py.Repr(ErrValidation)}; message was {Py.Repr(message)}");
        Ensure(message == LocalRegionMessage, $"expected AWS's exact ValidationException message, got {Py.Repr(message)}");
        Ensure(
            message.Contains("local region", StringComparison.Ordinal),
            $"the Delete form was refused, but not for the local-region reason: {Py.Repr(message)}");

        var described = (await ddb.DescribeTableAsync(table)).Table;
        Ensure(
            described.TableStatus == TableStatus.ACTIVE,
            $"table is {Py.Repr(described.TableStatus?.Value)} after a REFUSED ReplicaUpdates Delete of its own "
            + "region -- the refusal must not have started a teardown");
        Ensure(
            described.Replicas is not { Count: > 0 },
            $"table reports replicas {Py.Repr(described.Replicas?.Select(r => r.RegionName).ToList())} after both ReplicaUpdates forms "
            + "were refused");
        var listed = (await AllTableNames(ddb)).Where(n => n == table).ToList();
        Ensure(
            listed.SequenceEqual([table]),
            $"ListTables in {ctx.Region} reports {Py.Repr(listed)} for this table after the refusals");
        var survived = await GetItem(ddb, table, "survivor");
        Ensure(
            Py.Equal(survived, item),
            $"item is {Py.Repr(survived)} after a REFUSED ReplicaUpdates Delete of the local region; "
            + $"expected the untouched {Py.Repr(item)}");

        // Positive control: a legal UpdateTable on the same table succeeds.
        var updated = (await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_AND_OLD_IMAGES },
        })).TableDescription;
        Ensure(
            updated.StreamSpecification?.StreamViewType == StreamViewType.NEW_AND_OLD_IMAGES,
            $"UpdateTable accepted the stream change but returned "
            + $"{Py.Repr(StreamDict(updated.StreamSpecification))}");
        await WaitTableActive(ddb, table);
        var after = (await ddb.DescribeTableAsync(table)).Table;
        Ensure(
            after.StreamSpecification?.StreamEnabled == true,
            $"DescribeTable reports StreamSpecification {Py.Repr(StreamDict(after.StreamSpecification))} after a "
            + "successful UpdateTable that enabled the stream");
        var final = await GetItem(ddb, table, "survivor");
        Ensure(
            Py.Equal(final, item),
            $"item is {Py.Repr(final)} after the legal UpdateTable; expected the untouched {Py.Repr(item)}");
    }

    private static Dictionary<string, object?>? StreamDict(StreamSpecification? spec) =>
        spec is null ? null : new(StringComparer.Ordinal)
        {
            ["StreamEnabled"] = spec.StreamEnabled,
            ["StreamViewType"] = spec.StreamViewType?.Value,
        };
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

    private static Task WaitTableActive(IAmazonDynamoDB ddb, string table, double timeoutSeconds = 180) =>
        PollUntil(
            async () => (await ddb.DescribeTableAsync(table)).Table.TableStatus?.Value,
            s => s == "ACTIVE",
            timeoutSeconds,
            3);

    /// <summary>
    /// ConsistentRead, always: an eventually-consistent GetItem can serve a pre-write view and make
    /// "the item survived" pass or fail for a reason that is not the refusal under test.
    /// </summary>
    private static async Task<Dictionary<string, AttributeValue>?> GetItem(IAmazonDynamoDB ddb, string table, string key) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(key))), ConsistentRead = true })).Item;

    /// <summary>(error code, message) for a call AWS must refuse; an unexpected success raises.</summary>
    private static async Task<(string? Code, string Message)> Refusal<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"real AWS refuses this call, but it SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message ?? "");
    }

    /// <summary>Every table name; ListTables pages at 100 names, so page one alone can miss this table.</summary>
    private static async Task<List<string>> AllTableNames(IAmazonDynamoDB ddb)
    {
        var names = new List<string>();
        var request = new ListTablesRequest();
        while (true)
        {
            var page = await ddb.ListTablesAsync(request);
            names.AddRange(page.TableNames ?? []);
            var last = page.LastEvaluatedTableName;
            if (string.IsNullOrEmpty(last))
            {
                return names;
            }

            request = new ListTablesRequest { ExclusiveStartTableName = last };
        }
    }

    private static async Task DeleteTableIgnore(IAmazonDynamoDB ddb, string table)
    {
        var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(table));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }
}
