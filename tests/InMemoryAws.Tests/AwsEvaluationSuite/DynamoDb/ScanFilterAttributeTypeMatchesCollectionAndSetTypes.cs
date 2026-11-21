// Translated from behavioral/dynamodb/check_scan_filter_attribute_type_matches_collection_and_set_types.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-attribute-type-matches-collection-and-set-types</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterAttributeTypeMatchesCollectionAndSetTypes
{
    private const string Check = "scan-filter-attribute-type-matches-collection-and-set-types";

    /// <summary>
    /// pk -> the AttributeValue stored under "val", one per attribute_type code under test plus
    /// three scalar decoys and one item that lacks the attribute entirely.
    /// </summary>
    private static (string Pk, string Code, Func<AttributeValue> Val)[] ItemsUnderTest() =>
    [
        ("item-bs", "BS", () => BS([0x01], [0x02, 0x03])),
        ("item-l", "L", () => L(S("x"), N("1"))),
        ("item-ns", "NS", () => NS("1", "2", "3")),
        ("item-ss", "SS", () => SS("a", "b")),
        ("item-s", "S", () => S("hello")),
        ("item-n", "N", () => N("5")),
        ("item-bool", "BOOL", () => Bool(true)),
    ];

    /// <summary>The four codes under test; every expected set below is derived from the items, never typed.</summary>
    private static readonly string[] Codes = ["BS", "L", "NS", "SS"];

    /// <summary>An on-demand table keyed by pk (S) HASH only.</summary>
    private static async Task<string> MakeTable(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-behav-ddbat-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 90);
        return name;
    }

    private static async Task Seed(FidelityContext ctx, string name)
    {
        var ddb = ctx.Dynamo;
        foreach (var (pk, _, val) in ItemsUnderTest())
        {
            await ddb.PutItemAsync(name, Item(("pk", S(pk)), ("val", val())));
        }

        await ddb.PutItemAsync(name, Item(("pk", S("item-missing"))));
    }

    /// <summary>Every page of a strongly consistent Scan filtered on attribute_type(val, code).</summary>
    private static async Task<HashSet<string>> TypedPks(IAmazonDynamoDB ddb, string name, string code)
    {
        var items = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? start = null;
        while (true)
        {
            var page = await ddb.ScanAsync(new ScanRequest
            {
                TableName = name,
                FilterExpression = "attribute_type(#v, :t)",
                ExpressionAttributeNames = Names(("#v", "val")),
                ExpressionAttributeValues = Item((":t", S(code))),
                ConsistentRead = true,
                ExclusiveStartKey = start,
            });
            items.AddRange(page.Items);
            start = page.LastEvaluatedKey;
            if (start is null || start.Count == 0)
            {
                return items.Select(i => i["pk"].S).ToHashSet(StringComparer.Ordinal);
            }
        }
    }

    private static List<string> Sorted(IEnumerable<string> values) => [.. values.Order(StringComparer.Ordinal)];

    /// <summary>
    /// Scan's attribute_type(attr, code) matches exactly the item whose stored type is a Binary
    /// Set, List, Number Set or String Set, and excludes every item of a different stored type,
    /// including the three scalar types and the item that lacks the attribute.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ql-functions.attribute_type.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-attribute-type-matches-collection-and-set-types")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var name = await MakeTable(ctx);
        await Seed(ctx, name);

        var pkOf = ItemsUnderTest().Where(i => Codes.Contains(i.Code)).ToDictionary(i => i.Code, i => i.Pk, StringComparer.Ordinal);

        foreach (var code in Codes)
        {
            var got = await TypedPks(ddb, name, code);
            var want = new HashSet<string>(StringComparer.Ordinal) { pkOf[code] };
            Ensure(got.SetEquals(want), $"attribute_type(val, {Py.Repr(code)}) returned {Py.Repr(Sorted(got))}, expected {Py.Repr(Sorted(want))}");
        }

        // Positive control: the four matches partition exactly against the scalar types and the
        // item with no `val` attribute at all -- none of those four other items shows up under any
        // of the four collection/set codes.
        var union = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in Codes)
        {
            union.UnionWith(await TypedPks(ddb, name, code));
        }

        var expected = Codes.Select(c => pkOf[c]).ToList();
        Ensure(
            union.SetEquals(expected),
            $"the four collection/set codes together matched {Py.Repr(Sorted(union))}, expected exactly {Py.Repr(Sorted(expected))}");
    }
}
