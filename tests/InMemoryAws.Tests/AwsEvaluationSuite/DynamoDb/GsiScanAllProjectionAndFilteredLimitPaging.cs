// Translated from behavioral/dynamodb/check_gsi_scan_all_projection_and_filtered_limit_paging.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.gsi-scan-all-projection-and-filtered-limit-paging</summary>
[Category("AwsEvaluationSuite")]
public sealed class GsiScanAllProjectionAndFilteredLimitPaging
{
    private const string CheckId = "gsi-scan-all-projection-and-filtered-limit-paging";
    private const string Idx = "gsi-all";

    /// <summary>pk, gpk (GSI hash key), category, body.</summary>
    private static List<Dictionary<string, AttributeValue>> AllItems() =>
    [
        .. Enumerable.Range(0, 7).Select(i => Item(
            ("pk", S($"p{i}")),
            ("sk", S("fixed")),
            ("gpk", S($"g{i}")),
            ("category", S(i % 2 == 0 ? "even" : "odd")),
            ("body", S($"body{i}")))),
    ];

    private static List<string> EvenPks() =>
        [.. AllItems().Where(it => it["category"].S == "even").Select(it => it["pk"].S).Order(StringComparer.Ordinal)];

    private static string TupleRepr(IEnumerable<string> values)
    {
        var list = values.ToList();
        return "(" + string.Join(", ", list.Select(v => Py.Repr(v))) + (list.Count == 1 ? ",)" : ")");
    }

    /// <summary>
    /// Poll <paramref name="fn"/> until <paramref name="pred"/> holds: a GSI is eventually consistent, so this waits rather than
    /// sleeping a fixed amount.
    /// </summary>
    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutSeconds, double intervalSeconds = 2.0, string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        var value = await fn();
        while (!pred(value))
        {
            if (clock.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw Fail($"{description} not met within {timeoutSeconds}s. Last: {Py.Repr(value)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
            value = await fn();
        }

        return value;
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
    /// A Scan of an ALL-projection GSI returns every item attribute, and a FilterExpression with
    /// Limit paging over that GSI returns exactly the matching rows across pages.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.gsi-scan-all-projection-and-filtered-limit-paging")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-gsiscan-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("sk", ScalarAttributeType.S),
                new AttributeDefinition("gpk", ScalarAttributeType.S),
            ],
            GlobalSecondaryIndexes =
            [
                new GlobalSecondaryIndex
                {
                    IndexName = Idx,
                    KeySchema = [new KeySchemaElement("gpk", KeyType.HASH)],
                    Projection = new Projection { ProjectionType = ProjectionType.ALL },
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

        // --- ALL projection: the GSI serves every attribute of the base item, once it has caught up ---
        var allItems = await Poll(
            async () => (await ddb.ScanAsync(new ScanRequest { TableName = table, IndexName = Idx })).Items,
            items => items.Count == expectedItems.Count,
            timeoutSeconds: 120,
            description: $"all {expectedItems.Count} items propagating into {Idx}");
        var byPk = allItems.ToDictionary(it => it["pk"].S, it => it, StringComparer.Ordinal);
        foreach (var want in expectedItems)
        {
            var got = byPk.GetValueOrDefault(want["pk"].S);
            Ensure(Py.Equal(got, want), $"ALL GSI item for {Py.Repr(want["pk"].S)} is {Py.Repr(got)}, expected {Py.Repr(want)}");
        }

        // --- FilterExpression + Limit paging on the GSI returns exactly the matching rows ---
        var filtered = await ScanAllPages(ddb, () => new ScanRequest
        {
            TableName = table,
            IndexName = Idx,
            Limit = 2,
            FilterExpression = "category = :c",
            ExpressionAttributeValues = Item((":c", S("even"))),
        });
        var gotPks = filtered.Select(it => it["pk"].S).Order(StringComparer.Ordinal).ToList();
        var evenPks = EvenPks();
        Ensure(gotPks.SequenceEqual(evenPks), $"filtered+paginated GSI scan returned pks {TupleRepr(gotPks)}, expected {TupleRepr(evenPks)}");
        foreach (var it in filtered)
        {
            Ensure(Py.Equal(it["category"], S("even")), $"a non-matching item leaked through the filter: {Py.Repr(it)}");
        }
    }
}
