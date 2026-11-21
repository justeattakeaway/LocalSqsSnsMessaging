// Translated from behavioral/dynamodb/check_query_select_count_with_indexes_capacity_under_the_1mb_limit_omits_lastevaluatedkey.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-select-count-with-indexes-capacity-under-the-1mb-limit-omits-lastevaluatedkey</summary>
[Category("AwsEvaluationSuite")]
public sealed class QuerySelectCountWithIndexesCapacityUnderThe1mbLimitOmitsLastevaluatedkey
{
    private const string Check = "query-select-count-with-indexes-capacity-under-the-1mb-limit-omits-lastevaluatedkey";
    private const int ItemPayload = 100_000;
    private const int ItemCount = 4;
    private const int OneMb = 1024 * 1024;

    /// <summary>
    /// A GSI Query under the 1 MB page limit returns no LastEvaluatedKey, and Select=COUNT with
    /// ReturnConsumedCapacity=INDEXES reports the same Count and index CapacityUnits as a full read.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-select-count-with-indexes-capacity-under-the-1mb-limit-omits-lastevaluatedkey")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddb-qcix-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = "gsi1",
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(t), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        var payload = new string('x', ItemPayload);
        var sks = Enumerable.Range(0, ItemCount).Select(i => $"{i:000}").ToList();
        foreach (var sk in sks)
        {
            await ddb.PutItemAsync(t, Item(("pk", S("p")), ("sk", S(sk)), ("gpk", S("g")), ("payload", S(payload))));
        }

        Ensure(ItemCount * ItemPayload < OneMb, "the partition must stay comfortably under 1 MB so the limit never truncates it");

        QueryRequest Kwargs(Select? select = null) => new()
        {
            TableName = t,
            IndexName = "gsi1",
            KeyConditionExpression = "gpk = :g",
            ExpressionAttributeValues = Item((":g", S("g"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.INDEXES,
            Select = select,
        };

        // The GSI is eventually consistent: poll until it has caught up with every put above.
        var clock = FidelityTime.StartNew();
        const double deadline = 60;
        var baseline = await ddb.QueryAsync(Kwargs());
        while (baseline.Count < ItemCount && clock.Elapsed.TotalSeconds < deadline)
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
            baseline = await ddb.QueryAsync(Kwargs());
        }

        Ensure(
            Lek(baseline) is null,
            $"a {ItemCount}-item partition under 1 MB must return every item in one page with no " +
            $"LastEvaluatedKey, got {Py.Repr(Lek(baseline))}");
        Ensure(
            baseline.Count == baseline.ScannedCount && baseline.ScannedCount == ItemCount,
            $"expected Count == ScannedCount == {ItemCount}, got Count={Py.Repr(baseline.Count)} " +
            $"ScannedCount={Py.Repr(baseline.ScannedCount)}");
        var baseCc = baseline.ConsumedCapacity;
        Ensure(
            (baseCc.Table?.CapacityUnits ?? 0) == 0,
            $"a GSI query must charge the base table nothing, got {Py.Repr(Arm(baseCc.Table))}");
        var gsiUnits = baseCc.GlobalSecondaryIndexes["gsi1"].CapacityUnits;
        Ensure(gsiUnits > 0, $"the gsi1 arm must be charged, got {Py.Repr(gsiUnits)}");

        var counted = await ddb.QueryAsync(Kwargs(Select.COUNT));
        Ensure(counted.Items is not { Count: > 0 }, $"Select=COUNT must return no Items, got {Py.Repr(counted.Items)}");
        Ensure(
            Lek(counted) is null,
            "Select=COUNT over the same under-the-limit partition must also omit " +
            $"LastEvaluatedKey, got {Py.Repr(Lek(counted))}");
        Ensure(
            counted.Count == baseline.Count,
            $"Select=COUNT Count {Py.Repr(counted.Count)} must equal the full read's Count " +
            $"{Py.Repr(baseline.Count)}");
        Ensure(
            counted.ScannedCount == baseline.ScannedCount,
            $"Select=COUNT ScannedCount {Py.Repr(counted.ScannedCount)} must equal the full read's " +
            $"ScannedCount {Py.Repr(baseline.ScannedCount)}");
        var countedCc = counted.ConsumedCapacity;
        Ensure(
            (countedCc.Table?.CapacityUnits ?? 0) == 0,
            "Select=COUNT on a GSI query must still charge the base table nothing, got " +
            $"{Py.Repr(Arm(countedCc.Table))}");
        var countedGsiUnits = countedCc.GlobalSecondaryIndexes["gsi1"].CapacityUnits;
        Ensure(
            countedGsiUnits == gsiUnits,
            $"Select=COUNT billed the gsi1 arm {Py.Repr(countedGsiUnits)} CapacityUnits, the full read " +
            $"billed {Py.Repr(gsiUnits)} -- INDEXES capacity must price the bytes scanned, independent of " +
            "Select");

        // Positive control: the base table itself still holds every item written, read back whole.
        var got = await ddb.GetItemAsync(new GetItemRequest { TableName = t, Key = Item(("pk", S("p")), ("sk", S(sks[0]))), ConsistentRead = true });
        Ensure(
            got.Item.TryGetValue("gpk", out var gpk) && Py.Equal(gpk, S("g")),
            $"expected gpk 'g' on the base item, got {Py.Repr(got.Item.GetValueOrDefault("gpk"))}");
    }

    private static Dictionary<string, AttributeValue>? Lek(QueryResponse resp) => resp.LastEvaluatedKey is { Count: > 0 } key ? key : null;

    /// <summary>A capacity arm as boto3 hands it back: <c>{'CapacityUnits': 0.0}</c>.</summary>
    private static Dictionary<string, object?>? Arm(Capacity? capacity) =>
        capacity is null ? null : new() { ["CapacityUnits"] = capacity.CapacityUnits };

    /// <summary>A cleanup running undo() where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}
