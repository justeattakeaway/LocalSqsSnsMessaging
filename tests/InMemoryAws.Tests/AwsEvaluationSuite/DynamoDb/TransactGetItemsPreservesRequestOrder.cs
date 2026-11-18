// Translated from behavioral/dynamodb/check_transact_get_items_preserves_request_order.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-get-items-preserves-request-order</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactGetItemsPreservesRequestOrder
{
    private const string CheckId = "transact-get-items-preserves-request-order";

    /// <summary>
    /// TransactGetItems returns item responses in the exact order of the requested
    /// TransactItems, including an empty entry for a key that does not exist.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-get-items-preserves-request-order")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = await CreateTable(ctx);

        for (var i = 1; i < 4; i++)
        {
            await ddb.PutItemAsync(tableName, Item(("pk", S($"item-{i}")), ("val", S($"value-{i}"))));
        }

        // Deliberately out-of-order, interleaved with a key that does not exist.
        List<string> requestedOrder = ["item-3", "item-missing", "item-1", "item-2"];
        var resp = await ddb.TransactGetItemsAsync(new TransactGetItemsRequest
        {
            TransactItems = [.. requestedOrder.Select(k => new TransactGetItem { Get = new Get { TableName = tableName, Key = Item(("pk", S(k))) } })],
        });
        var responses = resp.Responses;
        Ensure(
            responses.Count == requestedOrder.Count,
            $"expected {requestedOrder.Count} responses, got {responses.Count}");

        foreach (var (key, entry) in requestedOrder.Zip(responses))
        {
            if (key == "item-missing")
            {
                Ensure(
                    entry.Item is null || entry.Item.Count == 0,
                    $"expected empty response entry for missing key, got {Py.Repr(Slot(entry))}");
            }
            else
            {
                var item = entry.Item;
                Ensure(item is not null, $"expected an item for {key}, got empty response");
                Ensure(
                    item["pk"].S == key,
                    $"order mismatch: expected {key} at this position, got {item["pk"].S}");
                var expectedVal = $"value-{key.Split('-')[1]}";
                Ensure(
                    item["val"].S == expectedVal,
                    $"expected val={expectedVal} for {key}, got {Py.Repr(item["val"])}");
            }
        }

        // Sanity: a second traversal in a different order must reorder the responses
        // accordingly, proving the order tracks the request rather than some fixed
        // internal ordering (e.g. insertion order).
        var reversedOrder = Enumerable.Reverse(requestedOrder).ToList();
        var resp2 = await ddb.TransactGetItemsAsync(new TransactGetItemsRequest
        {
            TransactItems = [.. reversedOrder.Select(k => new TransactGetItem { Get = new Get { TableName = tableName, Key = Item(("pk", S(k))) } })],
        });
        var gotKeys = resp2.Responses
            .Select(entry => entry.Item is not null && entry.Item.TryGetValue("pk", out var pk) && pk.S is not null ? pk.S : "item-missing")
            .ToList();
        Ensure(
            gotKeys.SequenceEqual(reversedOrder),
            $"reversed request order not reflected: expected {Py.Repr(reversedOrder)}, got {Py.Repr(gotKeys)}");
    }

    private static Dictionary<string, object?> Slot(ItemResponse response) =>
        response.Item is null ? [] : new() { ["Item"] = response.Item };

    private static async Task<string> CreateTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-tgi-");
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
