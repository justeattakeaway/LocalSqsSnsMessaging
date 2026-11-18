// Translated from behavioral/dynamodb/check_transact_get_items.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.transact-get-items</summary>
[Category("AwsEvaluationSuite")]
public sealed class TransactGetItems
{
    private const string CheckId = "transact-get-items";

    /// <summary>
    /// TransactGetItems retrieves multiple items atomically, and -- because it performs a
    /// two-phase strongly-consistent read to guard against a concurrent write -- consumes exactly
    /// twice the read capacity of the equivalent standalone GetItem calls on the same items.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.transact-get-items")]
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
            Tags = [new Tag { Key = "sim2real-check", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        await ddb.PutItemAsync(table, Item(("pk", S("tg-1")), ("v", N("10"))));
        await ddb.PutItemAsync(table, Item(("pk", S("tg-2")), ("v", N("20"))));

        var baselineCapacity = 0.0;
        foreach (var key in new[] { "tg-1", "tg-2" })
        {
            var single = await ddb.GetItemAsync(new GetItemRequest
            {
                TableName = table,
                Key = Item(("pk", S(key))),
                ConsistentRead = true,
                ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
            });
            baselineCapacity += single.ConsumedCapacity.CapacityUnits!.Value;
        }

        var resp = await ddb.TransactGetItemsAsync(new TransactGetItemsRequest
        {
            TransactItems =
            [
                new TransactGetItem { Get = new Get { TableName = table, Key = Item(("pk", S("tg-1"))) } },
                new TransactGetItem { Get = new Get { TableName = table, Key = Item(("pk", S("tg-2"))) } },
            ],
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
        });
        var responses = resp.Responses;
        Ensure(responses.Count == 2, $"expected 2 TransactGetItems responses, got {responses.Count}");
        Ensure(Py.Equal(responses[0].Item["v"], N("10")), $"expected tg-1 v=10, got {Py.Repr(new Dictionary<string, object?> { ["Item"] = responses[0].Item })}");
        Ensure(Py.Equal(responses[1].Item["v"], N("20")), $"expected tg-2 v=20, got {Py.Repr(new Dictionary<string, object?> { ["Item"] = responses[1].Item })}");

        var transactCapacity = resp.ConsumedCapacity.Sum(c => c.CapacityUnits!.Value);
        Ensure(
            transactCapacity == 2 * baselineCapacity,
            $"TransactGetItems must consume exactly twice the RCU of the equivalent standalone GetItem calls: expected {Py.Repr(2 * baselineCapacity)}, got {Py.Repr(transactCapacity)} (standalone baseline {Py.Repr(baselineCapacity)})");
    }
}
