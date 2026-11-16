// Translated from behavioral/dynamodb/check_ddbglobal_replica_delete_for_absent_replica_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.ddbglobal-replica-delete-for-absent-replica-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DdbglobalReplicaDeleteForAbsentReplicaRejected
{
    private const string CheckId = "ddbglobal-replica-delete-for-absent-replica-rejected";
    private const string ErrValidation = "ValidationException";

    /// <summary>
    /// UpdateTable ReplicaUpdates Delete for a region with no replica is a ValidationException
    /// "replica not found", distinct from the local-region refusal; the table stays ACTIVE and usable.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_UpdateTable.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.ddbglobal-replica-delete-for-absent-replica-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var regionB = DerivedSecondRegion(ctx);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbglobal-absent-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_AND_OLD_IMAGES },
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => DeleteTableIgnore(ddb, table));
        await WaitTableActive(ddb, table);
        var item = Item(("pk", S("untouched")), ("payload", S("survives-a-refused-replica-removal")), ("n", N("5")));
        await ddb.PutItemAsync(table, item);
        var before = await GetItem(ddb, table, "untouched");
        Ensure(Py.Equal(before, item), $"read back {Py.Repr(before)} immediately after writing {Py.Repr(item)}");

        // ESTABLISH the precondition rather than assuming it: the replica really is absent, so the
        // refusal below is about an absent replica and not about a stale one.
        var described = (await ddb.DescribeTableAsync(table)).Table;
        var present = (described.Replicas ?? []).Select(r => r.RegionName).ToList();
        Ensure(
            !present.Contains(regionB),
            $"derived partner region {Py.Repr(regionB)} already holds a replica of this freshly created "
            + $"table; DescribeTable reported replicas {Py.Repr(present)}");

        var (absentCode, absentMessage) = await RefusalPastCrossRegionToken(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            ReplicaUpdates = [new ReplicationGroupUpdate { Delete = new DeleteReplicationGroupMemberAction { RegionName = regionB } }],
        }));
        Ensure(
            absentCode == ErrValidation,
            $"ReplicaUpdates Delete for the absent replica in {Py.Repr(regionB)} returned {Py.Repr(absentCode)}, "
            + $"expected {Py.Repr(ErrValidation)}; message was {Py.Repr(absentMessage)}");
        const string expectedAbsent = "Replica specified in the Replica Update or Replica Delete action of the request was "
            + "not found.";
        Ensure(
            absentMessage == expectedAbsent,
            $"the removal was refused with {Py.Repr(absentMessage)}, expected {Py.Repr(expectedAbsent)}");

        // The refusal is REGION-SPECIFIC, not blanket: handed the table's own region instead, the same
        // operation is refused for a different, distinguishable reason. Only the difference is asserted
        // here -- the local-region contract itself is a separate check.
        var (localCode, localMessage) = await Refusal(() => ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            ReplicaUpdates = [new ReplicationGroupUpdate { Delete = new DeleteReplicationGroupMemberAction { RegionName = ctx.Region } }],
        }));
        Ensure(
            localCode == ErrValidation,
            $"ReplicaUpdates Delete naming {Py.Repr(ctx.Region)} returned {Py.Repr(localCode)}, expected "
            + $"{Py.Repr(ErrValidation)}; message was {Py.Repr(localMessage)}");
        const string expectedLocal = "Cannot add or delete the local region through ReplicaUpdates. Use CreateTable, "
            + "DeleteTable, or UpdateTable as required.";
        Ensure(
            localMessage == expectedLocal,
            $"ReplicaUpdates Delete naming {Py.Repr(ctx.Region)} was refused with {Py.Repr(localMessage)}, "
            + $"expected {Py.Repr(expectedLocal)}");
        Ensure(
            localMessage != absentMessage,
            $"ReplicaUpdates Delete gives the SAME message for the table's own region and for a "
            + $"region with no replica ({Py.Repr(absentMessage)}) -- the region argument is not being resolved");

        // EFFECT ABSENT: the refused removals changed nothing.
        var after = (await ddb.DescribeTableAsync(table)).Table;
        Ensure(
            after.TableStatus == TableStatus.ACTIVE,
            $"table is {Py.Repr(after.TableStatus?.Value)} after two REFUSED ReplicaUpdates Delete calls");
        Ensure(
            after.Replicas is not { Count: > 0 },
            $"table reports replicas {Py.Repr(after.Replicas?.Select(r => r.RegionName).ToList())} after two refused removals");
        var listed = (await AllTableNames(ddb)).Where(n => n == table).ToList();
        Ensure(
            listed.SequenceEqual([table]),
            $"ListTables in {ctx.Region} reports {Py.Repr(listed)} for this table after the refusals");
        var survived = await GetItem(ddb, table, "untouched");
        Ensure(
            Py.Equal(survived, item),
            $"item is {Py.Repr(survived)} after the refused removals; expected the untouched {Py.Repr(item)}");

        // POSITIVE CONTROL: the data plane still accepts and returns new writes after the refusals, so
        // the table was left fully functional rather than merely present.
        var fresh = Item(("pk", S("written-after")), ("payload", S("table-is-still-writable")), ("n", N("6")));
        await ddb.PutItemAsync(table, fresh);
        var gotFresh = await GetItem(ddb, table, "written-after");
        Ensure(
            Py.Equal(gotFresh, fresh),
            $"a write issued after the refused removals read back {Py.Repr(gotFresh)}, wrote {Py.Repr(fresh)}");
    }
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

    /// <summary>
    /// <see cref="Refusal{T}"/>, retried while DynamoDB's own call to the partner region is refused for its token
    /// (AccessDeniedException wrapping UnrecognizedClientException), which is transient.
    /// </summary>
    private static async Task<(string? Code, string Message)> RefusalPastCrossRegionToken<T>(Func<Task<T>> fn, int attempts = 3, double backoffSeconds = 2.0)
        where T : class
    {
        for (var attempt = 1; attempt < attempts; attempt++)
        {
            var (code, message) = await Refusal(fn);
            if (!(code == "AccessDeniedException" && message.Contains("UnrecognizedClientException", StringComparison.Ordinal)))
            {
                return (code, message);
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(backoffSeconds * attempt));
        }

        return await Refusal(fn);
    }

    /// <summary>
    /// The partner region: us-east-1, or us-west-2 when ctx.region is us-east-1. Both are
    /// enabled in every account, so choosing one needs no EC2 call and reads no account state.
    /// </summary>
    private static string DerivedSecondRegion(FidelityContext ctx) => ctx.Region != "us-east-1" ? "us-east-1" : "us-west-2";
}
