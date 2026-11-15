// Translated from behavioral/dynamodb/check_batch_get_item_always_reports_unprocessed_keys.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-get-item-always-reports-unprocessed-keys</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchGetItemAlwaysReportsUnprocessedKeys
{
    /// <summary>
    /// BatchGetItem always carries UnprocessedKeys, shaped like RequestItems so a caller can feed it
    /// straight back. Asserted by actually re-passing it, not by inspecting the type.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-get-item-always-reports-unprocessed-keys")]
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
        await ddb.PutItemAsync(table, Item(("pk", S("a"))));
        var res = await ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new() { [table] = new KeysAndAttributes { Keys = [Item(("pk", S("a")))] } },
        });
        Ensure(
            res.UnprocessedKeys is not null,
            $"UnprocessedKeys must be present even when nothing was left over, got {Py.Repr(Present(res))}");
        Ensure(
            res.UnprocessedKeys.Count == 0,
            $"nothing was unprocessed, so the map must be empty rather than absent, got {Py.Repr(res.UnprocessedKeys.Keys)}");
        Ensure(res.Responses[table].Count == 1, $"expected the one item back, got {Py.Repr(res.Responses)}");
        // The contract is that the value is directly re-passable as RequestItems. Feeding the empty map
        // back must be accepted -- a target that returns a differently shaped placeholder fails here.
        var requestItems = new Dictionary<string, KeysAndAttributes>(StringComparer.Ordinal)
        {
            [table] = new KeysAndAttributes { Keys = [Item(("pk", S("a")))] },
        };
        foreach (var (name, keys) in res.UnprocessedKeys)
        {
            requestItems[name] = keys;
        }

        var again = await ddb.BatchGetItemAsync(new BatchGetItemRequest { RequestItems = requestItems });
        Ensure(again.Responses[table].Count == 1, "re-passing UnprocessedKeys must be accepted");
    }

    /// <summary><c>sorted(res)</c>: the response members present.</summary>
    private static List<string> Present(BatchGetItemResponse res)
    {
        var present = new List<string> { "ResponseMetadata" };
        if (res.ConsumedCapacity is not null)
        {
            present.Add("ConsumedCapacity");
        }

        if (res.Responses is not null)
        {
            present.Add("Responses");
        }

        if (res.UnprocessedKeys is not null)
        {
            present.Add("UnprocessedKeys");
        }

        return [.. present.Order(StringComparer.Ordinal)];
    }
}
