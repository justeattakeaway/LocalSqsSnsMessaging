// Translated from behavioral/dynamodb/check_lsi_scan_all_and_include_projections_with_consistent_read.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.lsi-scan-all-and-include-projections-with-consistent-read</summary>
[Category("AwsEvaluationSuite")]
public sealed class LsiScanAllAndIncludeProjectionsWithConsistentRead
{
    private const string CheckId = "lsi-scan-all-and-include-projections-with-consistent-read";
    private const string AllIdx = "lsi-all";
    private const string IncludeIdx = "lsi-include";
    private static readonly string[] IncludeMembers = ["extra", "lskI", "pk", "sk"];

    /// <summary>pk, lskA (ALL-LSI range key), lskI (INCLUDE-LSI range key), category, extra, body.</summary>
    private static List<Dictionary<string, AttributeValue>> AllItems() =>
    [
        .. Enumerable.Range(0, 7).Select(i => Item(
            ("pk", S($"p{i}")),
            ("sk", S("fixed")),
            ("lskA", N(i.ToString(CultureInfo.InvariantCulture))),
            ("lskI", N(i.ToString(CultureInfo.InvariantCulture))),
            ("category", S(i % 2 == 0 ? "even" : "odd")),
            ("extra", S($"extra{i}")),
            ("body", S($"body{i}")))),
    ];

    private static string TupleRepr(IEnumerable<string> values)
    {
        var list = values.ToList();
        return "(" + string.Join(", ", list.Select(v => Py.Repr(v))) + (list.Count == 1 ? ",)" : ")");
    }

    /// <summary>Every item of a Scan, following LastEvaluatedKey.</summary>
    private static async Task<List<Dictionary<string, AttributeValue>>> ScanAllPages(IAmazonDynamoDB ddb, Func<ScanRequest> request)
    {
        var items = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? start = null;
        var guard = 0;
        while (true)
        {
            var kw = request();
            if (start is { Count: > 0 })
            {
                kw.ExclusiveStartKey = start;
            }

            var page = await ddb.ScanAsync(kw);
            items.AddRange(page.Items ?? []);
            start = page.LastEvaluatedKey;
            guard++;
            Ensure(guard < 50, "scan paging did not terminate");
            if (start is null || start.Count == 0)
            {
                return items;
            }
        }
    }

    /// <summary>
    /// A Scan of an ALL-projection LSI returns every item attribute; a Scan of an INCLUDE-projection
    /// LSI returns only the key attributes plus the included one; both are ConsistentRead=True, and a
    /// FilterExpression with Limit paging on the ALL LSI returns exactly the matching rows.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.lsi-scan-all-and-include-projections-with-consistent-read")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-lsiscan-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("lskA", ScalarAttributeType.N),
                new AttributeDefinition("lskI", ScalarAttributeType.N),
            ],
            LocalSecondaryIndexes =
            [
                new LocalSecondaryIndex
                {
                    IndexName = AllIdx,
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lskA", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
                },
                new LocalSecondaryIndex
                {
                    IndexName = IncludeIdx,
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("lskI", KeyType.RANGE)],
                    Projection = new Projection { ProjectionType = ProjectionType.INCLUDE, NonKeyAttributes = ["extra"] },
                },
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var expectedItems = AllItems();
        foreach (var item in expectedItems)
        {
            await ddb.PutItemAsync(table, item);
        }

        // --- ALL projection: the LSI serves every attribute of the base item ---
        var allItems = (await ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = AllIdx, ConsistentRead = true })).Items;
        Ensure(allItems.Count == expectedItems.Count, $"the ALL LSI returned {allItems.Count} item(s), expected {expectedItems.Count}");
        var byPk = allItems.ToDictionary(it => it["pk"].S, it => it, StringComparer.Ordinal);
        foreach (var want in expectedItems)
        {
            var got = byPk.GetValueOrDefault(want["pk"].S);
            Ensure(Py.Equal(got, want), $"ALL LSI item for {Py.Repr(want["pk"].S)} is {Py.Repr(got)}, expected {Py.Repr(want)}");
        }

        // --- INCLUDE projection: the LSI serves only the key attributes plus 'extra' ---
        var includeItems = (await ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = IncludeIdx, ConsistentRead = true })).Items;
        Ensure(includeItems.Count == expectedItems.Count, $"the INCLUDE LSI returned {includeItems.Count} item(s), expected {expectedItems.Count}");
        foreach (var it in includeItems)
        {
            var members = it.Keys.Order(StringComparer.Ordinal).ToList();
            Ensure(
                members.SequenceEqual(IncludeMembers),
                $"an INCLUDE LSI must project the table key, its own range key and the named attribute and nothing else, got {Py.Repr(members)}");
            var expectedExtra = S($"extra{int.Parse(it["lskI"].N, CultureInfo.InvariantCulture)}");
            Ensure(
                Py.Equal(it["extra"], expectedExtra),
                $"the INCLUDEd attribute for lskI={Py.Repr(it["lskI"].N)} is {Py.Repr(it["extra"])}, expected {Py.Repr(expectedExtra)}");
        }

        // --- FilterExpression + Limit paging on the ALL LSI returns exactly the matching rows ---
        var filtered = await ScanAllPages(ddb, () => new ScanRequest
        {
            TableName = table,
            IndexName = AllIdx,
            ConsistentRead = true,
            Limit = 2,
            FilterExpression = "category = :c",
            ExpressionAttributeValues = Item((":c", S("even"))),
        });
        var gotPks = filtered.Select(it => it["pk"].S).Order(StringComparer.Ordinal).ToList();
        var evenPks = expectedItems.Where(it => it["category"].S == "even").Select(it => it["pk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(gotPks.SequenceEqual(evenPks), $"filtered+paginated ALL LSI scan returned pks {TupleRepr(gotPks)}, expected {TupleRepr(evenPks)}");
        foreach (var it in filtered)
        {
            Ensure(Py.Equal(it["category"], S("even")), $"a non-matching item leaked through the filter: {Py.Repr(it)}");
        }
    }
}
