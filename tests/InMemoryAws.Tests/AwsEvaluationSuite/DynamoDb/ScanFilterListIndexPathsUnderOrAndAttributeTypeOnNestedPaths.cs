// Translated from behavioral/dynamodb/check_scan_filter_list_index_paths_under_or_and_attribute_type_on_nested_paths.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.scan-filter-list-index-paths-under-or-and-attribute-type-on-nested-paths</summary>
[Category("AwsEvaluationSuite")]
public sealed class ScanFilterListIndexPathsUnderOrAndAttributeTypeOnNestedPaths
{
    private const string Check = "scan-filter-list-index-paths-under-or-and-attribute-type-on-nested-paths";

    /// <summary>Five items as (id, tags list, meta.created typed value or null for "meta has no created key").</summary>
    private static readonly (string Id, string[] Tags, (string Type, string Value)? Created)[] Rows =
    [
        ("i1", ["x", "y"], ("S", "2024")),
        ("i2", ["y", "x"], ("N", "5")),
        ("i3", ["z"], ("BOOL", "True")),
        ("i4", [], ("S", "2024")),
        ("i5", ["y"], null),
    ];

    private static Dictionary<string, AttributeValue> RowItem((string Id, string[] Tags, (string Type, string Value)? Created) row)
    {
        var (rid, tagList, created) = row;
        var meta = created is { } c
            ? M(("created", c.Type switch
            {
                "S" => S(c.Value),
                "N" => N(c.Value),
                _ => Bool(c.Value == "True"),
            }))
            : M(("other", S("1")));
        return Item(
            ("id", S(rid)),
            ("tags", L([.. tagList.Select(S)])),
            ("meta", meta));
    }

    /// <summary>A fresh table with the five rows, under one BatchWriteItem.</summary>
    private static async Task<string> Seed(FidelityContext ctx)
    {
        var ddb = ctx.Dynamo;
        var name = FidelityContext.UniqueName("sim2real-ddbsfl-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        await ddb.WaitForTableExists(name, delaySeconds: 2, maxAttempts: 250);
        var resp = await ddb.BatchWriteItemAsync(new BatchWriteItemRequest
        {
            RequestItems = new()
            {
                [name] = [.. Rows.Select(r => new WriteRequest { PutRequest = new PutRequest { Item = RowItem(r) } })],
            },
        });
        var unprocessed = resp.UnprocessedItems ?? [];
        Ensure(unprocessed.Count == 0, $"seeding BatchWriteItem left {Py.Repr(unprocessed)} unprocessed");
        return name;
    }

    /// <summary>Every item of a strongly consistent Scan, across pages.</summary>
    private static async Task<List<Dictionary<string, AttributeValue>>> ScanAll(
        IAmazonDynamoDB ddb, string table, string? filterExpression = null, Dictionary<string, AttributeValue>? values = null)
    {
        var items = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? start = null;
        while (true)
        {
            var page = await ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                ConsistentRead = true,
                FilterExpression = filterExpression,
                ExpressionAttributeValues = values,
                ExclusiveStartKey = start,
            });
            items.AddRange(page.Items);
            start = page.LastEvaluatedKey;
            if (start is not { Count: > 0 })
            {
                return items;
            }
        }
    }

    private static SortedSet<string> Ids(IEnumerable<Dictionary<string, AttributeValue>> items) =>
        new(items.Select(i => i["id"].S), StringComparer.Ordinal);

    private static string? TagAt((string Id, string[] Tags, (string Type, string Value)? Created) row, int index) =>
        row.Tags.Length > index ? row.Tags[index] : null;

    /// <summary>
    /// Scan's FilterExpression ORs two list-index document paths into one match set, and
    /// attribute_type resolves the exact stored type at a nested map leaf and at a list index.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.scan-filter-list-index-paths-under-or-and-attribute-type-on-nested-paths")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = await Seed(ctx);

        var baseline = Ids(await ScanAll(ddb, table));
        Ensure(
            baseline.SetEquals(Rows.Select(r => r.Id)),
            $"the unfiltered Scan returned {Py.Repr(baseline)}, expected every seeded id");

        var orMatch = new SortedSet<string>(
            Rows.Where(r => TagAt(r, 0) == "x" || TagAt(r, 1) == "x").Select(r => r.Id), StringComparer.Ordinal);
        var got = Ids(await ScanAll(ddb, table, "tags[0] = :x OR tags[1] = :x", Item((":x", S("x")))));
        Ensure(got.SetEquals(orMatch), $"the OR'd list-index filter returned {Py.Repr(got)}, expected {Py.Repr(orMatch)}");

        foreach (var typeName in new[] { "S", "N", "BOOL" })
        {
            var expected = new SortedSet<string>(
                Rows.Where(r => r.Created is { } c && c.Type == typeName).Select(r => r.Id), StringComparer.Ordinal);
            got = Ids(await ScanAll(ddb, table, "attribute_type(meta.created, :t)", Item((":t", S(typeName)))));
            Ensure(
                got.SetEquals(expected),
                $"attribute_type(meta.created, {Py.Repr(typeName)}) returned {Py.Repr(got)}, expected {Py.Repr(expected)}");
        }

        var listIndexTypeMatch = new SortedSet<string>(
            Rows.Where(r => TagAt(r, 0) is not null).Select(r => r.Id), StringComparer.Ordinal);
        got = Ids(await ScanAll(ddb, table, "attribute_type(tags[0], :t)", Item((":t", S("S")))));
        Ensure(
            got.SetEquals(listIndexTypeMatch),
            $"attribute_type(tags[0], 'S') returned {Py.Repr(got)}, expected {Py.Repr(listIndexTypeMatch)}");

        var gotItem = (await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = Item(("id", S("i3"))),
            ConsistentRead = true,
        })).Item;
        Ensure(Py.Equal(gotItem, RowItem(Rows[2])), $"GetItem i3 returned {Py.Repr(gotItem)}, expected {Py.Repr(RowItem(Rows[2]))}");
    }
}
