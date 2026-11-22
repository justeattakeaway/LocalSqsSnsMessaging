// Translated from behavioral/dynamodb/check_query_select_count_capacity_matches_full_read_across_multipage_1mb_boundaries_with_mixed_item_sizes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.query-select-count-capacity-matches-full-read-across-multipage-1mb-boundaries-with-mixed-item-sizes</summary>
[Category("AwsEvaluationSuite")]
public sealed class QuerySelectCountCapacityMatchesFullReadAcrossMultipage1mbBoundariesWithMixedItemSizes
{
    private const string Check = "query-select-count-capacity-matches-full-read-across-multipage-1mb-boundaries-with-mixed-item-sizes";
    private const int OneMb = 1024 * 1024;
    private const int MaxPages = 12;

    // Ascending sizes so the later items in the partition are larger than the earlier ones.
    private static readonly int[] Payloads = [100_000, 200_000, 300_000, 390_000, 390_000, 390_000, 390_000, 390_000];
    private static readonly string SentinelSk = $"{Payloads.Length:000}";

    /// <summary>
    /// Query with Select=COUNT over a partition of unevenly sized items pages at the same 1 MB
    /// boundaries as a full read, with matching Count, ScannedCount and ConsumedCapacity per page.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.query-select-count-capacity-matches-full-read-across-multipage-1mb-boundaries-with-mixed-item-sizes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var t = FidelityContext.UniqueName("sim2real-behav-ddb-qmix-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH), new KeySchemaElement("sk", KeyType.RANGE)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S), new AttributeDefinition("sk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(t), "ResourceNotFoundException"));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);

        for (var i = 0; i < Payloads.Length; i++)
        {
            await ddb.PutItemAsync(t, Item(("pk", S("big")), ("sk", S($"{i:000}")), ("payload", S(new string('x', Payloads[i])))));
        }

        // A sentinel item past the last real one makes "more data remains" unambiguous on the final
        // page too: without it, AWS inconsistently reports LastEvaluatedKey when the 1 MB limit is
        // reached exactly on the partition's last item.
        await ddb.PutItemAsync(t, Item(("pk", S("big")), ("sk", S(SentinelSk)), ("payload", S("y"))));
        Ensure(Payloads.Sum() > 2 * OneMb, "the partition must exceed 2 MB so at least two pages truncate before the last page");

        var baseline = await Walk(ddb, t, "big");
        Ensure(
            baseline.Count >= 3,
            "a partition of ascending item sizes above 2 MB must span at least 3 pages, got " +
            $"{baseline.Count}");
        for (var n = 1; n <= baseline.Count; n++)
        {
            var page = baseline[n - 1];
            Ensure(
                page.Count == page.ScannedCount && page.ScannedCount == page.Items.Count,
                $"baseline page {n}: Count {Py.Repr(page.Count)} ScannedCount {Py.Repr(page.ScannedCount)} " +
                $"for {page.Items.Count} items");
        }

        Ensure(
            Lek(baseline[^1]) is null,
            "the last baseline page must reach the end of the partition, got " +
            $"{Py.Repr(Lek(baseline[^1]))}");
        foreach (var page in baseline.SkipLast(1))
        {
            Ensure(Lek(page) is not null, "every page but the last must carry a LastEvaluatedKey");
        }

        var seen = baseline.SelectMany(p => p.Items).Select(it => it["sk"].S).Order(StringComparer.Ordinal).ToList();
        var expectedSeen = Enumerable.Range(0, Payloads.Length).Select(i => $"{i:000}").Append(SentinelSk).ToList();
        Ensure(seen.SequenceEqual(expectedSeen), $"baseline pages returned {Py.Repr(seen)}, expected every item plus the sentinel exactly once");

        var counted = await Walk(ddb, t, "big", select: Select.COUNT);
        Ensure(
            counted.Count == baseline.Count,
            $"Select=COUNT must page at the same boundaries: {counted.Count} pages vs the full read's " +
            $"{baseline.Count}");
        for (var n = 1; n <= counted.Count; n++)
        {
            var (c, b) = (counted[n - 1], baseline[n - 1]);
            Ensure(c.Items is not { Count: > 0 }, $"Select=COUNT page {n} must return no Items, got {Py.Repr(c.Items)}");
            Ensure(c.Count == b.Count, $"Select=COUNT page {n} Count {Py.Repr(c.Count)} must equal the full read's {Py.Repr(b.Count)}");
            Ensure(
                c.ScannedCount == b.ScannedCount,
                $"Select=COUNT page {n} ScannedCount {Py.Repr(c.ScannedCount)} must equal the full read's " +
                $"{Py.Repr(b.ScannedCount)}");
            Ensure(
                Py.Equal(Lek(c), Lek(b)),
                $"Select=COUNT page {n} LastEvaluatedKey {Py.Repr(Lek(c))} must equal the " +
                $"full read's {Py.Repr(Lek(b))}");
            Ensure(
                c.ConsumedCapacity.CapacityUnits == b.ConsumedCapacity.CapacityUnits,
                $"Select=COUNT page {n} billed {Py.Repr(c.ConsumedCapacity.CapacityUnits)} " +
                $"CapacityUnits, the full read billed {Py.Repr(b.ConsumedCapacity.CapacityUnits)} -- " +
                "unevenly sized items must still price identically by Select");
        }

        var totalRcu = counted.Sum(p => p.ConsumedCapacity.CapacityUnits ?? 0);
        Ensure(
            totalRcu == baseline.Sum(p => p.ConsumedCapacity.CapacityUnits ?? 0),
            "the total capacity billed across all Select=COUNT pages must equal the full read's total");
    }

    private static Dictionary<string, AttributeValue>? Lek(QueryResponse resp) => resp.LastEvaluatedKey is { Count: > 0 } key ? key : null;

    /// <summary>Every page of a Query over partition key <paramref name="pk"/>, following LastEvaluatedKey.</summary>
    private static async Task<List<QueryResponse>> Walk(IAmazonDynamoDB ddb, string table, string pk, Select? select = null)
    {
        var pages = new List<QueryResponse>();
        var request = new QueryRequest
        {
            TableName = table,
            KeyConditionExpression = "pk = :p",
            ExpressionAttributeValues = Item((":p", S(pk))),
            ConsistentRead = true,
            ReturnConsumedCapacity = ReturnConsumedCapacity.TOTAL,
            Select = select,
        };
        while (true)
        {
            var resp = await ddb.QueryAsync(request);
            pages.Add(resp);
            if (Lek(resp) is not { } next)
            {
                return pages;
            }

            Ensure(pages.Count < MaxPages, $"Query did not end after {MaxPages} pages");
            request.ExclusiveStartKey = next;
        }
    }

    /// <summary>A cleanup running undo() where a failed delete raises.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        var error = await FidelityContext.CaptureError(undo);
        if (error is not null && !notFound.Contains(error.Code))
        {
            throw error.Exception;
        }
    };
}
