// Translated from behavioral/dynamodb/check_update_item_subtraction_list_append_and_nested_path_cost_no_more_than_a_direct_write.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-subtraction-list-append-and-nested-path-cost-no-more-than-a-direct-write</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemSubtractionListAppendAndNestedPathCostNoMoreThanADirectWrite
{
    private const string Check = "update-item-subtraction-list-append-and-nested-path-cost-no-more-than-a-direct-write";

    /// <summary>A cleanup running undo() where a failed delete raises; the resource's own not-found code means it is already gone.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };

    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb, string name)
    {
        var table = FidelityContext.UniqueName(name);
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        return table;
    }

    /// <summary>Run the update and report whether it cost 1.0 CapacityUnits (as opposed to 2.0).</summary>
    private static async Task<bool> OneWcu(IAmazonDynamoDB ddb, string table, string expression, Dictionary<string, AttributeValue>? values = null)
    {
        var request = new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("p"))),
            UpdateExpression = expression,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        };
        if (values is not null)
        {
            request.ExpressionAttributeValues = values;
        }

        var resp = await ddb.UpdateItemAsync(request);
        var cap = resp.ConsumedCapacity.CapacityUnits;
        Ensure(cap is 1.0 or 2.0, $"update on {Py.Repr(table)} cost {Py.Repr(cap)} CapacityUnits");
        return cap == 1.0;
    }

    /// <summary>
    /// UpdateItem's ConsumedCapacity for a subtraction (x - y), a list_append and a
    /// nested-path source (SET c = m.z) matches, byte for byte, a plain SET of the literal value
    /// each one computes: none of the three costs anything beyond the resulting item's own size.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-subtraction-list-append-and-nested-path-cost-no-more-than-a-direct-write")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var key = Item(("pk", S("p")));

        // --- subtraction vs. a direct assignment of the same result ---
        var tableDirect = await CreateTable(ctx, ddb, "sim2real-behav-subdir-");
        var tableSub = await CreateTable(ctx, ddb, "sim2real-behav-subop-");

        Task ResetNum(string table, int fillerLen) => ddb.PutItemAsync(table, Item(
            ("pk", S("p")), ("f", S(new string('x', fillerLen))), ("num", N("5"))));

        async Task<bool> Direct1Wcu(int fillerLen)
        {
            await ResetNum(tableDirect, fillerLen);
            return await OneWcu(ddb, tableDirect, "SET num = :v", Item((":v", N("5"))));
        }

        async Task<bool> Sub1Wcu(int fillerLen)
        {
            await ResetNum(tableSub, fillerLen);
            return await OneWcu(ddb, tableSub, "SET num = :a - :b", Item((":a", N("10")), (":b", N("5"))));
        }

        var maxDirect = await UpdateSizeBisection.Search(Direct1Wcu, 0, 2000);
        var maxSub = await UpdateSizeBisection.Search(Sub1Wcu, 0, 2000);
        Ensure(
            maxSub == maxDirect,
            $"max filler at 1.0 CapacityUnits was {Py.Repr(maxDirect)} for a direct SET and {Py.Repr(maxSub)} for " +
            "a subtraction producing the identical result; expected the same boundary");
        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableSub, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(got["num"], N("5")), $"subtraction result read back as {Py.Repr(got["num"])}, expected N '5'");

        // --- list_append vs. a direct assignment of the appended list ---
        var tableLapd = await CreateTable(ctx, ddb, "sim2real-behav-lapdir-");
        var tableLap = await CreateTable(ctx, ddb, "sim2real-behav-lapop-");

        Task ResetList(string table, int fillerLen) => ddb.PutItemAsync(table, Item(
            ("pk", S("p")), ("f", S(new string('x', fillerLen))), ("l", L(S("a")))));

        async Task<bool> Lapd1Wcu(int fillerLen)
        {
            await ResetList(tableLapd, fillerLen);
            return await OneWcu(ddb, tableLapd, "SET l = :v", Item((":v", L(S("a"), S("b")))));
        }

        async Task<bool> Lap1Wcu(int fillerLen)
        {
            await ResetList(tableLap, fillerLen);
            return await OneWcu(ddb, tableLap, "SET l = list_append(l, :v)", Item((":v", L(S("b")))));
        }

        var maxLapd = await UpdateSizeBisection.Search(Lapd1Wcu, 0, 2000);
        var maxLap = await UpdateSizeBisection.Search(Lap1Wcu, 0, 2000);
        Ensure(
            maxLap == maxLapd,
            $"max filler at 1.0 CapacityUnits was {Py.Repr(maxLapd)} for a direct SET of the final list and " +
            $"{Py.Repr(maxLap)} for list_append producing the identical list; expected the same boundary");
        got = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableLap, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(got["l"], L(S("a"), S("b"))), $"list_append result read back as {Py.Repr(got["l"])}, expected ['a', 'b']");

        // --- nested-path source vs. a direct assignment of the same value ---
        var tableNestd = await CreateTable(ctx, ddb, "sim2real-behav-nestdir-");
        var tableNest = await CreateTable(ctx, ddb, "sim2real-behav-nestop-");

        Task ResetNest(string table, int fillerLen) => ddb.PutItemAsync(table, Item(
            ("pk", S("p")), ("f", S(new string('x', fillerLen))), ("m", M(("z", S("zzzzzzzzzz")))), ("c", S("orig"))));

        async Task<bool> Nestd1Wcu(int fillerLen)
        {
            await ResetNest(tableNestd, fillerLen);
            return await OneWcu(ddb, tableNestd, "SET c = :v", Item((":v", S("zzzzzzzzzz"))));
        }

        async Task<bool> Nest1Wcu(int fillerLen)
        {
            await ResetNest(tableNest, fillerLen);
            return await OneWcu(ddb, tableNest, "SET c = m.z");
        }

        var maxNestd = await UpdateSizeBisection.Search(Nestd1Wcu, 0, 2000);
        var maxNest = await UpdateSizeBisection.Search(Nest1Wcu, 0, 2000);
        Ensure(
            maxNest == maxNestd,
            $"max filler at 1.0 CapacityUnits was {Py.Repr(maxNestd)} for a direct SET of the literal value " +
            $"and {Py.Repr(maxNest)} for copying it from a nested path; expected the same boundary");
        got = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableNest, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(got["c"], S("zzzzzzzzzz")), $"nested-path copy read back as {Py.Repr(got["c"])}");
    }
}
