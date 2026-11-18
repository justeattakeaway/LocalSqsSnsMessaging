// Translated from behavioral/dynamodb/check_batch_get_items.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-get-items</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchGetItems
{
    /// <summary>BatchGetItem retrieves multiple items by key in one call.</summary>
    [Test]
    [DisplayName("dynamodb.batch-get-items")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        for (var i = 0; i < 5; i++)
        {
            await ddb.PutItemAsync(table, Item(("pk", S($"bg-{i}")), ("val", N(i.ToString(CultureInfo.InvariantCulture)))));
        }

        var resp = await ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new()
            {
                [table] = new KeysAndAttributes { Keys = [.. Enumerable.Range(0, 5).Select(i => Item(("pk", S($"bg-{i}"))))], ConsistentRead = true },
            },
        });
        var unprocessed = resp.UnprocessedKeys ?? [];
        Ensure(unprocessed.Count == 0, $"expected no unprocessed keys for a 5-key request, got {Py.Repr(unprocessed.Keys)}");
        var items = resp.Responses[table];
        Ensure(items.Count == 5, $"expected 5 items, got {items.Count}: {Py.Repr(items)}");
        var values = items.ToDictionary(item => item["pk"].S, item => item["val"].N, StringComparer.Ordinal);
        Ensure(
            Py.Equal(values, Enumerable.Range(0, 5).ToDictionary(i => $"bg-{i}", i => i.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal)),
            $"expected exact pk->val mapping, got {Py.Repr(values)}");
    }
}
