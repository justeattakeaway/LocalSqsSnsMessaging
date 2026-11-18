// Translated from behavioral/dynamodb/check_legacy_attributes_to_get_empty_item_and_whole_nested_values.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.legacy-attributes-to-get-empty-item-and-whole-nested-values</summary>
[Category("AwsEvaluationSuite")]
public sealed class LegacyAttributesToGetEmptyItemAndWholeNestedValues
{
    private const string CheckId = "legacy-attributes-to-get-empty-item-and-whole-nested-values";

    /// <summary>
    /// Legacy AttributesToGet naming none of an item's attributes returns an empty Item
    /// (GetItem) or an empty entry (BatchGetItem), not an absent one; a base-table Query and
    /// Scan project to exactly the named set, and a List or Map attribute comes back whole.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.legacy-attributes-to-get-empty-item-and-whole-nested-values")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-ddbattg-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        Dictionary<string, AttributeValue> Key() => Item(("pk", S("p1")), ("sk", S("s1")));
        AttributeValue TagsList() => L(S("red"), S("blue"));
        AttributeValue MetaMap() => M(("x", S("1")), ("y", S("2")));
        await ddb.PutItemAsync(table, Item(
            ("pk", S("p1")), ("sk", S("s1")), ("name", S("alpha")), ("status", S("active")),
            ("tags", TagsList()), ("meta", MetaMap())));

        // GetItem: the item exists but holds none of the two named attributes -- the response
        // carries an Item key whose value is an empty dict, not an absent Item.
        var noneMatching = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), AttributesToGet = ["phone", "address"], ConsistentRead = true });
        Ensure(noneMatching.Item is not null, "expected an Item key even though nothing matched: {}");
        Ensure(noneMatching.Item.Count == 0, $"expected an empty Item, got {Py.Repr(noneMatching.Item)}");

        // Positive control: the same item, naming an attribute it does have.
        var control = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), AttributesToGet = ["name"], ConsistentRead = true });
        Ensure(Py.Equal(control.Item, Item(("name", S("alpha")))), $"control GetItem returned {Py.Repr(control.Item)}");

        BatchGetItemRequest Batch(List<string> attributes) => new()
        {
            RequestItems = new Dictionary<string, KeysAndAttributes>(StringComparer.Ordinal)
            {
                [table] = new KeysAndAttributes { Keys = [Key()], AttributesToGet = attributes, ConsistentRead = true },
            },
        };

        // BatchGetItem: the per-item entry is an empty dict, present in the Responses list rather
        // than the item being dropped from it.
        var noneMatchingBatch = await ddb.BatchGetItemAsync(Batch(["phone", "address"]));
        Ensure(
            Py.Equal(noneMatchingBatch.Responses[table], new[] { Item() }),
            $"expected a single empty-dict entry, got {Py.Repr(noneMatchingBatch.Responses[table])}");

        // Positive control: same Keys, naming an attribute the item has.
        var controlBatch = await ddb.BatchGetItemAsync(Batch(["name"]));
        Ensure(
            Py.Equal(controlBatch.Responses[table], new[] { Item(("name", S("alpha"))) }),
            $"control BatchGetItem returned {Py.Repr(controlBatch.Responses[table])}");

        // Query on the base table: projects to exactly the named set, and the List/Map attributes
        // come back whole -- there is no way to name an element inside them.
        var keyConditions = new Dictionary<string, Condition>(StringComparer.Ordinal)
        {
            ["pk"] = new Condition { AttributeValueList = [S("p1")], ComparisonOperator = ComparisonOperator.EQ },
            ["sk"] = new Condition { AttributeValueList = [S("s1")], ComparisonOperator = ComparisonOperator.EQ },
        };
        var expectedProjection = new[] { Item(("name", S("alpha")), ("tags", TagsList()), ("meta", MetaMap())) };
        var queried = await ddb.QueryAsync(new QueryRequest { TableName = table, KeyConditions = keyConditions, AttributesToGet = ["name", "tags", "meta"], ConsistentRead = true });
        Ensure(Py.Equal(queried.Items, expectedProjection), $"Query with AttributesToGet=[name,tags,meta] returned {Py.Repr(queried.Items)}");

        // Scan: the same projection and wholeness rule, read off the whole table rather than a key.
        var scanned = await ddb.ScanAsync(new ScanRequest { TableName = table, AttributesToGet = ["name", "tags", "meta"], ConsistentRead = true });
        Ensure(scanned.Count == 1, $"expected the single seeded item, got Count={Py.Repr(scanned.Count)}");
        Ensure(Py.Equal(scanned.Items, expectedProjection), $"Scan with AttributesToGet=[name,tags,meta] returned {Py.Repr(scanned.Items)}");
    }
}
