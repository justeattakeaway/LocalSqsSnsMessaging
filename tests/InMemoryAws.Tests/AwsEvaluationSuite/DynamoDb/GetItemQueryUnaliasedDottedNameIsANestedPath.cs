// Translated from behavioral/dynamodb/check_get_item_query_unaliased_dotted_name_is_a_nested_path.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.get-item-query-unaliased-dotted-name-is-a-nested-path</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetItemQueryUnaliasedDottedNameIsANestedPath
{
    private const string CheckId = "get-item-query-unaliased-dotted-name-is-a-nested-path";

    /// <summary>
    /// An unaliased dot in GetItem/Query's ProjectionExpression is a nested path: it misses a
    /// top-level attribute literally named with that dot, which only an alias reaches. Legacy
    /// AttributesToGet names an attribute by its literal string and never splits on a dot.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.get-item-query-unaliased-dotted-name-is-a-nested-path")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbdot-");
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

        static Dictionary<string, AttributeValue> Key() => Item(("pk", S("rec1")));
        static AttributeValue FlatValue() => S("flat-value");
        static AttributeValue MetaMap() => M(("x", S("nested-value")), ("y", S("other")));
        await ddb.PutItemAsync(table, Item(("pk", S("rec1")), ("Safety.Warning", FlatValue()), ("meta", MetaMap())));

        // Unaliased: the dot is parsed as a path separator, so it looks for a top-level "Safety"
        // holding a "Warning" member -- neither exists, so nothing is projected.
        var unaliased = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true, ProjectionExpression = "Safety.Warning" });
        Ensure(
            Py.Equal(unaliased.Item, Item()),
            $"unaliased GetItem PE 'Safety.Warning' returned {Py.Repr(unaliased.Item)}, expected an empty Item because the dot is read as a nested path, not the flat attribute");

        // Aliased: the placeholder's value is the literal dotted string, naming the flat attribute.
        var aliased = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Key(),
            ConsistentRead = true,
            ProjectionExpression = "#sw",
            ExpressionAttributeNames = Names(("#sw", "Safety.Warning")),
        });
        Ensure(
            Py.Equal(aliased.Item, Item(("Safety.Warning", FlatValue()))),
            $"aliased GetItem PE '#sw' returned {Py.Repr(aliased.Item)}, expected the flat attribute {{'Safety.Warning': {Py.Repr(FlatValue())}}}");

        // Legacy AttributesToGet never parses a path: a dotted name only matches a top-level
        // attribute spelled exactly that way, so it misses the nested meta.x leaf entirely.
        var legacyDotted = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true, AttributesToGet = ["meta.x"] });
        Ensure(
            Py.Equal(legacyDotted.Item, Item()),
            $"GetItem AttributesToGet=['meta.x'] returned {Py.Repr(legacyDotted.Item)}, expected an empty Item -- 'meta.x' names a literal top-level attribute, not the meta.x leaf");

        // Positive control: the same legacy parameter naming the whole top-level map.
        var legacyWhole = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true, AttributesToGet = ["meta"] });
        Ensure(
            Py.Equal(legacyWhole.Item, Item(("meta", MetaMap()))),
            $"GetItem AttributesToGet=['meta'] returned {Py.Repr(legacyWhole.Item)}, expected the whole meta map {{'meta': {Py.Repr(MetaMap())}}}");

        // Query reproduces the same unaliased-vs-aliased split for the matched item.
        var queryUnaliased = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            ConsistentRead = true,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = Item((":pk", S("rec1"))),
            ProjectionExpression = "Safety.Warning",
        });
        Ensure(
            queryUnaliased.Count == 1 && Py.Equal(queryUnaliased.Items, new[] { Item() }),
            $"Query with unaliased PE 'Safety.Warning' returned Count={Py.Repr(queryUnaliased.Count)} Items={Py.Repr(queryUnaliased.Items)}, expected the matched item projected to {{}}");

        var queryAliased = await ddb.QueryAsync(new QueryRequest
        {
            TableName = table,
            ConsistentRead = true,
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = Item((":pk", S("rec1"))),
            ProjectionExpression = "#sw",
            ExpressionAttributeNames = Names(("#sw", "Safety.Warning")),
        });
        Ensure(
            queryAliased.Count == 1 && Py.Equal(queryAliased.Items, new[] { Item(("Safety.Warning", FlatValue())) }),
            $"Query with aliased PE '#sw' returned Count={Py.Repr(queryAliased.Count)} Items={Py.Repr(queryAliased.Items)}, expected the flat attribute on the matched item");
    }
}
