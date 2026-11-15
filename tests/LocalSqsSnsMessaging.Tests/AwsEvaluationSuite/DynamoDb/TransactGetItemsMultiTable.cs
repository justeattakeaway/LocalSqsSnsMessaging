// Translated from behavioral/dynamodb/check_transact_get_items_multi_table.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-get-items-multi-table</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactGetItemsMultiTable
{
    private const string CheckId = "transact-get-items-multi-table";

    /// <summary>
    /// TransactGetItems returns a single atomic-snapshot response spanning MULTIPLE distinct
    /// tables, each Get slot resolved against the correct table and in request order (a missing key
    /// resolves to an empty slot, not an error).
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-get-items-multi-table")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableA = await SimpleTable(ctx, ddb, "sim2real-behav-tgi-a-");
        var tableB = await SimpleTable(ctx, ddb, "sim2real-behav-tgi-b-");
        await ddb.PutItemAsync(tableA, Item(("pk", S("x")), ("src", S("a"))));
        await ddb.PutItemAsync(tableB, Item(("pk", S("x")), ("src", S("b"))));

        var resp = await ddb.TransactGetItemsAsync(new TransactGetItemsRequest
        {
            TransactItems =
            [
                new TransactGetItem { Get = new Get { TableName = tableA, Key = Item(("pk", S("x"))) } },
                new TransactGetItem { Get = new Get { TableName = tableB, Key = Item(("pk", S("x"))) } },
                new TransactGetItem { Get = new Get { TableName = tableA, Key = Item(("pk", S("does-not-exist"))) } },
            ],
        });
        var responses = resp.Responses;
        Ensure(responses.Count == 3, $"expected 3 response slots, got {responses.Count}: {Py.Repr(responses.Select(Slot).ToList())}");
        Ensure(
            Py.Equal(responses[0].Item?["src"], S("a")),
            $"slot 0 must resolve against table_a, got {Py.Repr(Slot(responses[0]))}");
        Ensure(
            Py.Equal(responses[1].Item?["src"], S("b")),
            $"slot 1 must resolve against table_b, got {Py.Repr(Slot(responses[1]))}");
        Ensure(
            Slot(responses[2]).Count == 0,
            $"missing key on table_a must resolve to an empty slot, not an error, got {Py.Repr(Slot(responses[2]))}");
    }

    /// <summary>The response slot as boto3 hands it back: <c>{'Item': {...}}</c> or <c>{}</c>.</summary>
    private static Dictionary<string, object?> Slot(ItemResponse response) =>
        response.Item is null ? [] : new() { ["Item"] = response.Item };

    private static async Task<string> SimpleTable(FidelityContext ctx, IAmazonDynamoDB ddb, string prefix)
    {
        var name = FidelityContext.UniqueName(prefix);
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }
}
