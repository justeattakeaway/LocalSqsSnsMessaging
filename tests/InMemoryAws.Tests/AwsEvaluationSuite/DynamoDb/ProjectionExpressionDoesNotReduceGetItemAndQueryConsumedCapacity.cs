// Translated from behavioral/dynamodb/check_projection_expression_does_not_reduce_get_item_and_query_consumed_capacity.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.projection-expression-does-not-reduce-get-item-and-query-consumed-capacity</summary>
[Category("AwsEvaluationSuite")]
public sealed class ProjectionExpressionDoesNotReduceGetItemAndQueryConsumedCapacity
{
    private const string CheckId = "projection-expression-does-not-reduce-get-item-and-query-consumed-capacity";

    /// <summary>
    /// Sized so the whole item is exactly 9216 bytes (9 KiB): a strongly consistent read of it bills
    /// exactly ceil(9216 / 4096) = 3.0 RCU, with or without a projection.
    /// </summary>
    private const int BlobLen = 9202;

    private static readonly string[] AllKeys = ["pk", "tag", "blob"];
    private static readonly string[] TagOnly = ["tag"];

    private static string SortedKeys(Dictionary<string, AttributeValue> item) => Py.Repr(item.Keys.Order(StringComparer.Ordinal).ToList());

    /// <summary>
    /// A ProjectionExpression that returns one attribute bills the same CapacityUnits as
    /// returning the whole item: GetItem and Query meter the item read, not the response.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.projection-expression-does-not-reduce-get-item-and-query-consumed-capacity")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-projcap-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        await ddb.PutItemAsync(table, Item(
            ("pk", S("item")),
            ("tag", S("x")),
            ("blob", S(new string('y', BlobLen)))));

        var fullGet = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("item"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
            ConsistentRead = true,
        });
        var fullGetCap = fullGet.ConsumedCapacity.CapacityUnits;
        Ensure(fullGetCap == 3.0, $"a strongly consistent GetItem of the 9216-byte item billed {Py.Repr(fullGetCap)}, expected 3.0");
        Ensure(
            fullGet.Item.Keys.ToHashSet().SetEquals(AllKeys),
            $"the unprojected GetItem returned keys {SortedKeys(fullGet.Item)}, expected all three");

        var projectedGet = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("item"))),
            ProjectionExpression = "tag",
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
            ConsistentRead = true,
        });
        var projectedGetCap = projectedGet.ConsumedCapacity.CapacityUnits;
        Ensure(
            projectedGet.Item.Keys.ToHashSet().SetEquals(TagOnly),
            $"the projected GetItem returned keys {SortedKeys(projectedGet.Item)}, expected only 'tag'");
        Ensure(
            projectedGetCap == fullGetCap,
            $"a one-attribute projection billed {Py.Repr(projectedGetCap)}, the unprojected read {Py.Repr(fullGetCap)} -- a projection must not reduce GetItem capacity");

        var fullQuery = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :v",
            ExpressionAttributeValues = Item((":v", S("item"))),
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
            ConsistentRead = true,
        });
        var fullQueryCap = fullQuery.ConsumedCapacity.CapacityUnits;
        Ensure(fullQuery.Count == 1, $"the query matched {fullQuery.Count} items, expected 1");
        Ensure(fullQueryCap == 3.0, $"a strongly consistent Query of the 9216-byte item billed {Py.Repr(fullQueryCap)}, expected 3.0");
        Ensure(
            fullQuery.Items[0].Keys.ToHashSet().SetEquals(AllKeys),
            $"the unprojected Query item had keys {SortedKeys(fullQuery.Items[0])}, expected all three");

        var projectedQuery = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :v",
            ExpressionAttributeValues = Item((":v", S("item"))),
            ProjectionExpression = "tag",
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
            ConsistentRead = true,
        });
        var projectedQueryCap = projectedQuery.ConsumedCapacity.CapacityUnits;
        Ensure(
            projectedQuery.Items[0].Keys.ToHashSet().SetEquals(TagOnly),
            $"the projected Query item had keys {SortedKeys(projectedQuery.Items[0])}, expected only 'tag'");
        Ensure(
            projectedQueryCap == fullQueryCap,
            $"a one-attribute projection billed {Py.Repr(projectedQueryCap)}, the unprojected query {Py.Repr(fullQueryCap)} -- a projection must not reduce Query capacity");
    }
}
