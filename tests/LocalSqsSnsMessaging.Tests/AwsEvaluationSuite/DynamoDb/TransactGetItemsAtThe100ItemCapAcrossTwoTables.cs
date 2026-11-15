// Translated from behavioral/dynamodb/check_transact_get_items_at_the_100_item_cap_across_two_tables.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-get-items-at-the-100-item-cap-across-two-tables</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactGetItemsAtThe100ItemCapAcrossTwoTables
{
    private const string CheckId = "transact-get-items-at-the-100-item-cap-across-two-tables";

    private static async Task<string> CreateTable(FidelityContext ctx, IAmazonDynamoDB ddb, string prefix)
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

    /// <summary>
    /// TransactGetItems at the 100-action limit, split across two tables, returns
    /// exactly 100 Responses in request order with each slot resolved against its own table.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-get-items-at-the-100-item-cap-across-two-tables")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableA = await CreateTable(ctx, ddb, "sim2real-behav-tgi100-a-");
        var tableB = await CreateTable(ctx, ddb, "sim2real-behav-tgi100-b-");

        var transactItems = new List<TransactGetItem>();
        for (var i = 0; i < 100; i++)
        {
            var table = i % 2 == 0 ? tableA : tableB;
            var pk = $"k{i}";
            await ddb.PutItemAsync(table, Item(("pk", S(pk)), ("slot", N(i.ToString(CultureInfo.InvariantCulture)))));
            transactItems.Add(new TransactGetItem { Get = new Get { TableName = table, Key = Item(("pk", S(pk))) } });
        }

        var resp = await ddb.TransactGetItemsAsync(new TransactGetItemsRequest { TransactItems = transactItems });
        var responses = resp.Responses;
        Ensure(responses.Count == 100, $"expected 100 response slots, got {responses.Count}: {Py.Repr(responses.Select(r => r.Item).ToList())}");
        for (var i = 0; i < responses.Count; i++)
        {
            var item = responses[i].Item;
            Ensure(item is not null, $"slot {i} came back empty, expected the item put at request index {i}");
            var got = item["slot"].N;
            var want = i.ToString(CultureInfo.InvariantCulture);
            Ensure(got == want, $"slot {i} carried N {Py.Repr(got)}, expected the request-order value {Py.Repr(want)}");
        }
    }
}
