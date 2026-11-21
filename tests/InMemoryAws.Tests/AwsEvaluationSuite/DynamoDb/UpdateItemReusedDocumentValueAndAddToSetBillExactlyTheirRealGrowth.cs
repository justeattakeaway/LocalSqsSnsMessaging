// Translated from behavioral/dynamodb/check_update_item_reused_document_value_and_add_to_set_bill_exactly_their_real_growth.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.update-item-reused-document-value-and-add-to-set-bill-exactly-their-real-growth</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateItemReusedDocumentValueAndAddToSetBillExactlyTheirRealGrowth
{
    private const string Check = "update-item-reused-document-value-and-add-to-set-bill-exactly-their-real-growth";
    private const string NewMember = "cccccccccc";

    private static AttributeValue Doc() => M(("x", S("y")));

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

    private static async Task<bool> OneWcu(IAmazonDynamoDB ddb, string table, string expression, Dictionary<string, AttributeValue> values)
    {
        var resp = await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("p"))),
            UpdateExpression = expression,
            ExpressionAttributeValues = values,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var cap = resp.ConsumedCapacity.CapacityUnits;
        Ensure(cap is 1.0 or 2.0, $"update on {Py.Repr(table)} cost {Py.Repr(cap)} CapacityUnits");
        return cap == 1.0;
    }

    /// <summary>
    /// A document value bound once and SET into two attributes is billed for its full stored size
    /// at each place it lands, never deduplicated; ADD to a set bills by the resulting membership, so
    /// adding an already-present member costs the same as adding that member to a set without it.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.update-item-reused-document-value-and-add-to-set-bill-exactly-their-real-growth")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;

        // --- a document :v used once vs. the same :v used at two targets ---
        var tableOnce = await CreateTable(ctx, ddb, "sim2real-behav-docreuse1-");
        var tableTwice = await CreateTable(ctx, ddb, "sim2real-behav-docreuse2-");

        Task ResetPair(string table, int fillerLen) => ddb.PutItemAsync(table, Item(
            ("pk", S("p")), ("f", S(new string('x', fillerLen))), ("a", S("orig")), ("b", S("orig"))));

        async Task<bool> Once1Wcu(int fillerLen)
        {
            await ResetPair(tableOnce, fillerLen);
            return await OneWcu(ddb, tableOnce, "SET a = :v", Item((":v", Doc())));
        }

        async Task<bool> Twice1Wcu(int fillerLen)
        {
            await ResetPair(tableTwice, fillerLen);
            return await OneWcu(ddb, tableTwice, "SET a = :v, b = :v", Item((":v", Doc())));
        }

        var maxOnce = await UpdateSizeBisection.Search(Once1Wcu, 0, 2000);
        var maxTwice = await UpdateSizeBisection.Search(Twice1Wcu, 0, 2000);
        Ensure(
            maxTwice < maxOnce,
            $"max filler at 1.0 CapacityUnits was {Py.Repr(maxOnce)} writing the document once and " +
            $"{Py.Repr(maxTwice)} writing it at two targets; expected the second use to cost strictly more");

        // The only real difference between the two updates is that "b" changes from the 5-byte
        // string "orig" to the 7-byte document, a net growth of 2 bytes -- exactly the boundary shift.
        Ensure(
            maxOnce - maxTwice == 2,
            $"the boundary shifted by {Py.Repr(maxOnce - maxTwice)} bytes between using the document once " +
            "and twice, expected exactly 2 (the real growth of the second attribute alone)");

        var got = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableTwice, Key = Item(("pk", S("p"))), ConsistentRead = true })).Item;
        Ensure(
            Py.Equal(got["a"], Doc()) && Py.Equal(got["b"], Doc()),
            $"both targets must hold the reused document; got a={Py.Repr(got["a"])} b={Py.Repr(got["b"])}");

        // --- ADD to a set: a brand-new member vs. a member already present (a no-op) ---
        var tableNew = await CreateTable(ctx, ddb, "sim2real-behav-addnew-");
        var tableNoop = await CreateTable(ctx, ddb, "sim2real-behav-addnoop-");

        Task ResetWithoutMember(string table, int fillerLen) => ddb.PutItemAsync(table, Item(
            ("pk", S("p")), ("f", S(new string('x', fillerLen))), ("s", SS("a", "b"))));

        Task ResetWithMember(string table, int fillerLen) => ddb.PutItemAsync(table, Item(
            ("pk", S("p")), ("f", S(new string('x', fillerLen))), ("s", SS("a", "b", NewMember))));

        async Task<bool> AddNew1Wcu(int fillerLen)
        {
            await ResetWithoutMember(tableNew, fillerLen);
            return await OneWcu(ddb, tableNew, "ADD s :v", Item((":v", SS(NewMember))));
        }

        async Task<bool> AddNoop1Wcu(int fillerLen)
        {
            await ResetWithMember(tableNoop, fillerLen);
            return await OneWcu(ddb, tableNoop, "ADD s :v", Item((":v", SS(NewMember))));
        }

        var maxNew = await UpdateSizeBisection.Search(AddNew1Wcu, 0, 2000);
        var maxNoop = await UpdateSizeBisection.Search(AddNoop1Wcu, 0, 2000);
        Ensure(
            maxNew == maxNoop,
            $"max filler at 1.0 CapacityUnits was {Py.Repr(maxNew)} adding a brand-new member and " +
            $"{Py.Repr(maxNoop)} adding a member already present; expected the same boundary, since the " +
            "resulting membership is identical either way");

        var gotNew = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableNew, Key = Item(("pk", S("p"))), ConsistentRead = true })).Item;
        Ensure(
            gotNew["s"].SS is { Count: > 0 } newMembers && new HashSet<string>(newMembers, StringComparer.Ordinal).SetEquals(["a", "b", NewMember]),
            $"ADD of a new member left the set as {Py.Repr(gotNew["s"])}");
        var gotNoop = (await ddb.GetItemAsync(new GetItemRequest { TableName = tableNoop, Key = Item(("pk", S("p"))), ConsistentRead = true })).Item;
        Ensure(
            new HashSet<string>(gotNoop["s"].SS, StringComparer.Ordinal).SetEquals(["a", "b", NewMember]),
            $"ADD of an already-present member left the set as {Py.Repr(gotNoop["s"])}");
    }
}
