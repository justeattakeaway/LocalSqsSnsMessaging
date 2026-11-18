// Translated from behavioral/dynamodb/check_batch_get_item_multi_table_absent_keys_return_empty_list.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.batch-get-item-multi-table-absent-keys-return-empty-list</summary>
[Category("AwsEvaluationSuite")]
public sealed class BatchGetItemMultiTableAbsentKeysReturnEmptyList
{
    private const string Check = "batch-get-item-multi-table-absent-keys-return-empty-list";

    /// <summary>
    /// A BatchGetItem across two tables, naming only keys absent from each, returns an empty list
    /// under each table's own key in Responses rather than omitting the table or erroring.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.batch-get-item-multi-table-absent-keys-return-empty-list")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableA = await SimpleTable(ctx, ddb, "sim2real-behav-bgiabs-a-");
        var tableB = await SimpleTable(ctx, ddb, "sim2real-behav-bgiabs-b-");

        // Positive control: each table answers with its own present item before asserting on keys
        // that are absent from both.
        await ddb.PutItemAsync(tableA, Item(("pk", S("present"))));
        await ddb.PutItemAsync(tableB, Item(("pk", S("present"))));
        var control = await ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new()
            {
                [tableA] = new KeysAndAttributes { Keys = [Item(("pk", S("present")))], ConsistentRead = true },
                [tableB] = new KeysAndAttributes { Keys = [Item(("pk", S("present")))], ConsistentRead = true },
            },
        });
        Ensure(Py.Equal(control.Responses[tableA], new[] { Item(("pk", S("present"))) }), Py.Repr(control.Responses[tableA]));
        Ensure(Py.Equal(control.Responses[tableB], new[] { Item(("pk", S("present"))) }), Py.Repr(control.Responses[tableB]));

        var resp = await ddb.BatchGetItemAsync(new BatchGetItemRequest
        {
            RequestItems = new()
            {
                [tableA] = new KeysAndAttributes { Keys = [Item(("pk", S("missing-a1"))), Item(("pk", S("missing-a2")))], ConsistentRead = true },
                [tableB] = new KeysAndAttributes { Keys = [Item(("pk", S("missing-b1")))], ConsistentRead = true },
            },
        });
        Ensure(
            resp.Responses.GetValueOrDefault(tableA) is { Count: 0 },
            $"table_a with no matching keys must map to an empty list, got {Py.Repr(resp.Responses.GetValueOrDefault(tableA))}");
        Ensure(
            resp.Responses.GetValueOrDefault(tableB) is { Count: 0 },
            $"table_b with no matching keys must map to an empty list, got {Py.Repr(resp.Responses.GetValueOrDefault(tableB))}");
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
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        return name;
    }
}
