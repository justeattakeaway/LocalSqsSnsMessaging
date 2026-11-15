// Translated from behavioral/dynamodb/check_numeric_key_is_canonicalized_on_every_path.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.numeric-key-is-canonicalized-on-every-path</summary>
[Category("AwsEvaluationSuite")]
public sealed class NumericKeyIsCanonicalizedOnEveryPath
{
    private const string CheckId = "numeric-key-is-canonicalized-on-every-path";
    private static readonly string[] Spellings = ["42", "00042", "42.0", "4.2e1"];

    /// <summary>
    /// A Number key written as 00042 is stored canonical by PutItem, BatchWriteItem and TransactWriteItems, and GetItem,
    /// UpdateItem and DeleteItem reach it through a different spelling, so every write and read path canonicalizes.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.numeric-key-is-canonicalized-on-every-path")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-numkey-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.N)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        async Task<Dictionary<string, AttributeValue>?> Get(string spelling) =>
            (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Item(("pk", N(spelling))), ConsistentRead = true })).Item;

        // --- PutItem writes canonical; four spellings of the key all reach it ---
        await ddb.PutItemAsync(table, Item(("pk", N("42")), ("via", S("put"))));
        foreach (var spelling in Spellings)
        {
            var found = await Get(spelling);
            Ensure(
                Py.Equal(found, Item(("pk", N("42")), ("via", S("put")))),
                $"GetItem with the key spelled {Py.Repr(spelling)} must resolve to the item stored under the canonical key 42, got {Py.Repr(found)}");
        }

        // --- BatchWriteItem indexes a non-canonical key canonically ---
        await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal)
            {
                [table] = [new WriteRequest { PutRequest = new PutRequest { Item = Item(("pk", N("00043")), ("via", S("batch"))) } }],
            },
        });
        var batched = await Get("43");
        Ensure(
            Py.Equal(batched, Item(("pk", N("43")), ("via", S("batch")))),
            $"an item written by BatchWriteItem with the key spelled 00043 must be reachable as 43 and store the canonical key, got {Py.Repr(batched)}");

        // --- TransactWriteItems does the same ---
        await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest
        {
            TransactItems = [new TransactWriteItem { Put = new Put { TableName = table, Item = Item(("pk", N("0044.00")), ("via", S("transact"))) } }],
        });
        var transacted = await Get("44");
        Ensure(
            Py.Equal(transacted, Item(("pk", N("44")), ("via", S("transact")))),
            $"an item written by TransactWriteItems with the key spelled 0044.00 must be reachable as 44 and store the canonical key, got {Py.Repr(transacted)}");

        // --- UpdateItem addressed by a non-canonical key reaches the same item ---
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", N("004.2e1"))),
            UpdateExpression = "SET via = :v",
            ExpressionAttributeValues = Item((":v", S("updated"))),
        });
        var updated = await Get("42");
        Ensure(
            Py.Equal(updated, Item(("pk", N("42")), ("via", S("updated")))),
            $"UpdateItem keyed by 004.2e1 must update the item stored under 42 rather than creating a second one, got {Py.Repr(updated)}");

        // --- DeleteItem resolves its key too, and returns the canonical form ---
        var removed = await ddb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = table,
            Key = Item(("pk", N("0042.0"))),
            ReturnValues = ReturnValue.ALL_OLD,
        });
        Ensure(
            Py.Equal(removed.Attributes, Item(("pk", N("42")), ("via", S("updated")))),
            $"DeleteItem keyed by 0042.0 must delete the item stored under 42 and echo the canonical key, got {Py.Repr(removed.Attributes)}");
        Ensure(await Get("42") is null, $"the item must really be gone after the non-canonical delete, found {Py.Repr(await Get("42"))}");

        // --- the other two paths' items are untouched by that delete ---
        Ensure(
            await Get("43") is not null && await Get("44") is not null,
            $"deleting 42 must not disturb the other keys: 43 -> {Py.Repr(await Get("43"))}, 44 -> {Py.Repr(await Get("44"))}");
    }
}
