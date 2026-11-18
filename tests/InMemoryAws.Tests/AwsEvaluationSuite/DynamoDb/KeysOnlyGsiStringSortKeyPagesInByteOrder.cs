// Translated from behavioral/dynamodb/check_keys_only_gsi_string_sort_key_pages_in_byte_order.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.keys-only-gsi-string-sort-key-pages-in-byte-order</summary>
[Category("AwsEvaluationSuite")]
public sealed class KeysOnlyGsiStringSortKeyPagesInByteOrder
{
    private const string CheckId = "keys-only-gsi-string-sort-key-pages-in-byte-order";
    private const string Index = "by-group-label";
    private const int Page = 3;

    /// <summary>Sort-key strings whose byte order differs from numeric and case-folded order.</summary>
    private static readonly string[] Labels = ["10", "9", "B", "a", "é", "a b", "Z", "~", "A", "á"];
    private static readonly string[] Groups = ["7", "-1"];
    private static readonly string[] KeyAttributes = ["id", "grp", "label"];

    private static int CompareBytes(byte[] x, byte[] y) => x.AsSpan().SequenceCompareTo(y);

    /// <summary>
    /// A KEYS_ONLY GSI with a numeric partition and string sort key pages by Limit=3 through
    /// LastEvaluatedKey to exactly one group's indexed items, in UTF-8 byte order of the sort key,
    /// each carrying only the table and index keys; items without the sort key are never indexed.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.keys-only-gsi-string-sort-key-pages-in-byte-order")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-gsi-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
            AttributeDefinitions =
            [
                new AttributeDefinition("id", ScalarAttributeType.S),
                new AttributeDefinition("grp", ScalarAttributeType.N),
                new AttributeDefinition("label", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Index,
                    KeySchema = [new KeySchemaElement("grp", KeyType.HASH), new KeySchemaElement("label", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => DeleteTableIfExists(ddb, table));
        await WaitActive(ddb, table);

        var expected = new Dictionary<string, List<(byte[] Bytes, string Id, string Label)>>(StringComparer.Ordinal) { ["7"] = [], ["-1"] = [] };
        for (var i = 0; i < Labels.Length; i++)
        {
            var label = Labels[i];
            foreach (var grp in Groups)
            {
                var itemId = $"{grp}/{i.ToString("00", CultureInfo.InvariantCulture)}";
                var item = Item(("id", S(itemId)), ("grp", N(grp)), ("label", S(label)), ("payload", S($"p{i}")));
                expected[grp].Add((Encoding.UTF8.GetBytes(label), itemId, label));
                await ddb.PutItemAsync(table, item);
            }

            // A sparse item: it has the partition attribute but no sort key, so it is not indexed.
            await ddb.PutItemAsync(table, Item(("id", S($"sparse-{i}")), ("grp", N("7"))));
        }

        var want = expected.ToDictionary(
            e => e.Key,
            e => e.Value
                .OrderBy(r => r.Bytes, Comparer<byte[]>.Create(CompareBytes))
                .ThenBy(r => r.Id, StringComparer.Ordinal)
                .ThenBy(r => r.Label, StringComparer.Ordinal)
                .Select(r => (r.Id, r.Label))
                .ToList(),
            StringComparer.Ordinal);

        foreach (var grp in Groups)
        {
            var forward = await PollFull(ddb, table, grp, want[grp].Count, forward: true);
            Ensure(
                forward.SequenceEqual(want[grp]),
                $"KEYS_ONLY GSI Query for grp={grp} with Limit={Page} collected {Py.Repr(forward)}, expected {Py.Repr(want[grp])} in byte order of the label");
            var backward = await Collect(ddb, table, grp, forward: false);
            Ensure(
                backward.SequenceEqual(Enumerable.Reverse(want[grp])),
                $"ScanIndexForward=False for grp={grp} collected {Py.Repr(backward)}, expected the reverse");
        }
    }

    /// <summary>
    /// Every (id, label) of one GSI partition, following LastEvaluatedKey; each page holds &lt;= Limit
    /// items with exactly the key attributes.
    /// </summary>
    private static async Task<List<(string, string)>> Collect(IAmazonDynamoDB ddb, string table, string grp, bool forward)
    {
        var output = new List<(string, string)>();
        Dictionary<string, AttributeValue>? start = null;
        var pages = 0;
        while (true)
        {
            var page = await ddb.QueryAsync(new QueryRequest
            {
                TableName = table,
                IndexName = Index,
                Limit = Page,
                KeyConditionExpression = "grp = :g",
                ExpressionAttributeValues = Item((":g", N(grp))),
                ScanIndexForward = forward,
                ExclusiveStartKey = start,
            });
            var items = page.Items;
            Ensure(items.Count <= Page, $"a page held {items.Count} items, more than Limit={Page}");
            Ensure(page.Count == items.Count, $"page Count {Py.Repr(page.Count)} differs from its {items.Count} items");
            foreach (var i in items)
            {
                Ensure(i.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(KeyAttributes), $"a KEYS_ONLY index item carried {Py.Repr(i.Keys.Order(StringComparer.Ordinal).ToList())}");
                Ensure(Py.Equal(i["grp"], N(grp)), $"an item of another partition came back: {Py.Repr(i)}");
            }

            output.AddRange(items.Select(i => (i["id"].S, i["label"].S)));
            pages++;
            Ensure(pages <= 20, "LastEvaluatedKey never ran out after 20 pages");
            start = page.LastEvaluatedKey;
            if (start is null || start.Count == 0)
            {
                return output;
            }

            Ensure(
                start.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(KeyAttributes),
                $"a GSI LastEvaluatedKey carries the table and index keys, got {Py.Repr(start.Keys.Order(StringComparer.Ordinal).ToList())}");
        }
    }

    /// <summary>GSIs are eventually consistent: collect until the partition holds all n items.</summary>
    private static async Task<List<(string, string)>> PollFull(IAmazonDynamoDB ddb, string table, string grp, int n, bool forward)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var got = await Collect(ddb, table, grp, forward);
            if (got.Count >= n || clock.Elapsed >= TimeSpan.FromSeconds(40))
            {
                return got;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task WaitActive(IAmazonDynamoDB ddb, string table, double timeoutSeconds = 40.0)
    {
        var clock = FidelityTime.StartNew();
        TableDescription? last = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            last = (await ddb.DescribeTableAsync(table)).Table;
            var indexes = last.GlobalSecondaryIndexes ?? [];
            if (last.TableStatus == TableStatus.ACTIVE && indexes.Count > 0 && indexes.All(g => g.IndexStatus == IndexStatus.ACTIVE))
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"table {table} or its GSI did not become ACTIVE: {Py.Repr(last)}");
    }

    private static async Task DeleteTableIfExists(IAmazonDynamoDB ddb, string table)
    {
        try
        {
            await ddb.DeleteTableAsync(table);
        }
        catch (ResourceNotFoundException)
        {
        }
    }
}
