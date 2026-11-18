// Translated from behavioral/dynamodb/check_batch_get_multi_table.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-get-multi-table</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchGetMultiTable
{
    /// <summary>
    /// A single BatchGetItem call spanning TWO different tables returns each table's matched items
    /// under its own key in the Responses map, correctly attributed and with no cross-table bleed.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-get-multi-table")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableA = await SimpleTable(ctx, ddb, "sim2real-behav-bgi-a-");
        var tableB = await SimpleTable(ctx, ddb, "sim2real-behav-bgi-b-");

        await ddb.PutItemAsync(tableA, Item(("pk", S("k1")), ("from", S("a"))));
        await ddb.PutItemAsync(tableA, Item(("pk", S("k2")), ("from", S("a"))));
        await ddb.PutItemAsync(tableB, Item(("pk", S("k1")), ("from", S("b"))));

        var resp = await ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new()
            {
                [tableA] = new KeysAndAttributes { Keys = [Item(("pk", S("k1"))), Item(("pk", S("k2")))], ConsistentRead = true },
                [tableB] = new KeysAndAttributes { Keys = [Item(("pk", S("k1"))), Item(("pk", S("missing")))], ConsistentRead = true },
            },
        });
        var responses = resp.Responses;
        var pksA = responses[tableA].Select(it => it["pk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(Py.Equal(pksA, new[] { "k1", "k2" }), $"table_a expected keys ['k1', 'k2'], got {Py.Repr(pksA)}");
        Ensure(responses[tableA].All(it => Py.Equal(it["from"], S("a"))), Py.Repr(responses[tableA]));
        var pksB = responses[tableB].Select(it => it["pk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(Py.Equal(pksB, new[] { "k1" }), $"table_b expected only ['k1'] ('missing' has no item), got {Py.Repr(pksB)}");
        Ensure(Py.Equal(responses[tableB][0]["from"], S("b")), Py.Repr(responses[tableB]));
        Ensure((resp.UnprocessedKeys ?? []).Count == 0, Py.Repr(resp.UnprocessedKeys?.Keys));
    }

    private static async Task<string> SimpleTable(FidelityContext ctx, IAmazonDynamoDB ddb, string prefix)
    {
        var name = FidelityContext.UniqueName(prefix);
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }
}
