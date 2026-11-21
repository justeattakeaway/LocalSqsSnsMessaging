// Translated from behavioral/dynamodb/check_transact_write_items_delete_of_absent_item_is_a_no_op_that_still_commits.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-write-items-delete-of-absent-item-is-a-no-op-that-still-commits</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactWriteItemsDeleteOfAbsentItemIsANoOpThatStillCommits
{
    private const string Check = "transact-write-items-delete-of-absent-item-is-a-no-op-that-still-commits";

    /// <summary>
    /// A TransactWriteItems Delete with no ConditionExpression commits when its key has no item: the
    /// call succeeds with an empty body, the key stays absent, and a sibling Delete in the same
    /// transaction still removes the item it targets.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-write-items-delete-of-absent-item-is-a-no-op-that-still-commits")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbtx-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        await ddb.PutItemAsync(table, Item(("pk", S("existing")), ("v", S("present"))));
        await ddb.PutItemAsync(table, Item(("pk", S("survivor")), ("v", S("untouched"))));

        Ensure(await Get(ddb, table, "ghost") is null, "setup: 'ghost' must have no item before the transaction");
        Ensure(await Get(ddb, table, "existing") is not null, "setup: 'existing' must have an item before the transaction");

        var resp = await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem { Delete = new Delete { TableName = table, Key = Item(("pk", S("ghost"))) } },
                new TransactWriteItem { Delete = new Delete { TableName = table, Key = Item(("pk", S("existing"))) } },
            ],
        });
        var body = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (resp.ConsumedCapacity is not null)
        {
            body["ConsumedCapacity"] = resp.ConsumedCapacity.Select(c => c.TableName).ToList();
        }

        if (resp.ItemCollectionMetrics is not null)
        {
            body["ItemCollectionMetrics"] = resp.ItemCollectionMetrics.Keys.ToList();
        }

        Ensure(body.Count == 0, $"a TransactWriteItems of two unconditional Deletes returned a non-empty body: {Py.Repr(body)}");

        var ghostAfter = await Get(ddb, table, "ghost");
        Ensure(ghostAfter is null, $"deleting an absent key must not create it, got {Py.Repr(ghostAfter)}");

        var existingAfter = await Get(ddb, table, "existing");
        Ensure(existingAfter is null, $"the sibling Delete of 'existing' must have applied, got {Py.Repr(existingAfter)}");

        var survivor = await Get(ddb, table, "survivor");
        Ensure(
            Py.Equal(survivor, Item(("pk", S("survivor")), ("v", S("untouched")))),
            $"an item outside the transaction must be untouched, got {Py.Repr(survivor)}");
    }

    private static async Task<Dictionary<string, AttributeValue>?> Get(IAmazonDynamoDB ddb, string table, string pk) =>
        (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", S(pk))), ConsistentRead = true })).Item;
}
