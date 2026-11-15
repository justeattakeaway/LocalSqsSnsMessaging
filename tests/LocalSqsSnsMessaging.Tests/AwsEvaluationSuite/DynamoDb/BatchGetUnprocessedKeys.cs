// Translated from behavioral/dynamodb/check_batch_get_unprocessed_keys.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-get-unprocessed-keys</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchGetUnprocessedKeys
{
    /// <summary>
    /// BatchGetItem with a key for a nonexistent item omits it from Responses without error and never lists
    /// it in UnprocessedKeys: a missing item is not a deferred key.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-get-unprocessed-keys")]
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
            Tags = [new Tag { Key = "sim2real-check", Value = "batch-get-unprocessed-keys" }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("exists-1")), ("v", S("yes"))));
        var resp = await ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new()
            {
                [table] = new KeysAndAttributes { Keys = [Item(("pk", S("exists-1"))), Item(("pk", S("ghost-1")))], ConsistentRead = true },
            },
        });
        var items = resp.Responses[table];
        Ensure(items.Count == 1, $"expected exactly 1 existing item back, got {Py.Repr(items.Count)}: {Py.Repr(items)}");
        Ensure(items[0]["pk"].S == "exists-1", $"expected pk exists-1, got {Py.Repr(items[0]["pk"])}");
        var unprocessed = resp.UnprocessedKeys ?? [];
        Ensure(
            unprocessed.Count == 0,
            "a key for a non-existent item was reported as unprocessed instead of simply absent: " +
            $"{Py.Repr(unprocessed.Keys)}");
    }
}
