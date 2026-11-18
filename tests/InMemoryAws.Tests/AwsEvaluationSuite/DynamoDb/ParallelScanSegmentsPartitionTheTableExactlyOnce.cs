// Translated from behavioral/dynamodb/check_parallel_scan_segments_partition_the_table_exactly_once.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.parallel-scan-segments-partition-the-table-exactly-once</summary>
[Category("AwsEvaluationSuite")]
public sealed class ParallelScanSegmentsPartitionTheTableExactlyOnce
{
    private const string CheckId = "parallel-scan-segments-partition-the-table-exactly-once";
    private const int Items = 200;
    private const int TotalSegments = 8;
    private const int FreezeReads = 3;

    /// <summary>
    /// Return the botocore Error dict for a call real AWS refuses.
    ///
    /// Raises on unexpected success, so accepting invalid input fails distinctly.
    /// </summary>
    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"real AWS refuses this Scan, but it SUCCEEDED and returned {Py.Repr(result)}");
    }

    /// <summary>BatchWriteItem until UnprocessedItems is empty; raises if it never drains.</summary>
    private static async Task DrainBatch(IAmazonDynamoDB ddb, string table, List<WriteRequest> requests, int timeoutSeconds = 180)
    {
        var pending = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal) { [table] = requests };
        var clock = FidelityTime.StartNew();
        while (pending.Count > 0)
        {
            if (clock.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw Fail($"BatchWriteItem never drained UnprocessedItems within {timeoutSeconds}s; still pending {Py.Repr(pending)}");
            }

            pending = (await ddb.BatchWriteItemAsync(new BatchWriteItemRequest { RequestItems = pending })).UnprocessedItems ?? [];
            if (pending.Count > 0)
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
            }
        }
    }

    /// <summary>
    /// Every pk a (possibly segmented) Scan returns, in arrival order, duplicates KEPT.
    ///
    /// A list, not a set: a target that ignores Segment returns the same item from several segments,
    /// and a set-union would hide that.
    /// </summary>
    private static async Task<List<string>> ScanAll(IAmazonDynamoDB ddb, string table, int? totalSegments = null, int? segment = null)
    {
        var got = new List<string>();
        Dictionary<string, AttributeValue>? start = null;
        while (true)
        {
            var page = await ddb.ScanAsync(new ScanRequest
            {
                TableName = table,
                ConsistentRead = true,
                TotalSegments = totalSegments,
                Segment = segment,
                ExclusiveStartKey = start,
            });
            got.AddRange(page.Items.Select(item => item["pk"].S));
            if (page.LastEvaluatedKey is null)
            {
                return got;
            }

            start = page.LastEvaluatedKey;
        }
    }

    private static List<string> Sorted(IEnumerable<string> values) => [.. values.Order(StringComparer.Ordinal)];

    private static List<string> SymmetricDifference(IEnumerable<string> a, IEnumerable<string> b)
    {
        var set = a.ToHashSet(StringComparer.Ordinal);
        set.SymmetricExceptWith(b);
        return Sorted(set);
    }

    /// <summary>
    /// A parallel Scan partitions the table: every item is returned by exactly one segment, the union
    /// equals an unsegmented Scan, re-reading a segment returns the same items, and
    /// Segment == TotalSegments is refused with ValidationException.
    ///
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Scan.html#Scan.ParallelScan
    /// </summary>
    [Test]
    [DisplayName("dynamodb.parallel-scan-segments-partition-the-table-exactly-once")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var table = FidelityContext.UniqueName("sim2real-behav-parscan-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Tags = [new Tag { Key = "sim2real:managed", Value = "true" }, new Tag { Key = "sim2real:check", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var written = Enumerable.Range(0, Items).Select(i => "ITEM#" + i.ToString("0000", CultureInfo.InvariantCulture)).ToList();
        for (var offset = 0; offset < Items; offset += 25)
        {
            await DrainBatch(ddb, table, [.. written.Skip(offset).Take(25).Select(pk => new WriteRequest
            {
                PutRequest = new PutRequest { Item = Item(("pk", S(pk)), ("payload", S(new string('x', 32)))) },
            })]);
        }

        // --- reference: one unsegmented Scan sees everything. This is the positive control for the
        // --- per-segment absences below: if it were short, no segment claim would mean anything.
        var baseline = await ScanAll(ddb, table);
        Ensure(
            Sorted(baseline).SequenceEqual(written),
            $"an unsegmented strongly-consistent Scan must return the {Py.Repr(Items)} written items exactly once; it returned {Py.Repr(baseline.Count)} items, symmetric difference {Py.Repr(SymmetricDifference(baseline, written).Take(20).ToList())}");

        // --- the partition claim ---
        var perSegment = new Dictionary<int, List<string>>();
        for (var seg = 0; seg < TotalSegments; seg++)
        {
            perSegment[seg] = await ScanAll(ddb, table, TotalSegments, seg);
        }

        var flat = Enumerable.Range(0, TotalSegments).SelectMany(seg => perSegment[seg]).ToList();
        string Counts() => Py.Repr(perSegment.ToDictionary(p => p.Key, p => p.Value.Count));

        var duplicated = Sorted(flat.GroupBy(pk => pk, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key));
        Ensure(
            duplicated.Count == 0,
            $"parallel Scan segments must be DISJOINT: {Py.Repr(duplicated.Count)} of {Py.Repr(Items)} items were returned by more than one segment, e.g. {Py.Repr(duplicated.Take(10).ToList())}. Per-segment counts {Counts()}");
        Ensure(
            flat.Count == Items,
            $"parallel Scan segments must be EXHAUSTIVE: the {Py.Repr(TotalSegments)} segments returned {Py.Repr(flat.Count)} items in total, expected exactly {Py.Repr(Items)}. Per-segment counts {Counts()}");
        Ensure(
            Sorted(flat).SequenceEqual(Sorted(baseline)),
            $"the union of the segments must equal the unsegmented Scan; symmetric difference {Py.Repr(SymmetricDifference(flat, baseline).Take(20).ToList())}");
        var populated = perSegment.Where(p => p.Value.Count > 0).Select(p => p.Key).Order().ToList();

        // --- a Scan is a read, so re-reading a segment must not move it ---
        var probeSegment = populated[0];
        var first = Sorted(perSegment[probeSegment]);
        for (var attempt = 0; attempt < FreezeReads; attempt++)
        {
            var again = Sorted(await ScanAll(ddb, table, TotalSegments, probeSegment));
            Ensure(
                again.SequenceEqual(first),
                $"Scan is a read and must not advance any server-side cursor: repeat {Py.Repr(attempt + 1)} of segment {Py.Repr(probeSegment)} returned {Py.Repr(again.Count)} items, expected the same {Py.Repr(first.Count)}; symmetric difference {Py.Repr(SymmetricDifference(again, first).Take(10).ToList())}");
        }

        // --- always-invalid input: Segment must be < TotalSegments. No restore leg exists by
        // --- construction, so the positive control is the identical call one index lower.
        var highestValid = await ScanAll(ddb, table, TotalSegments, TotalSegments - 1);
        Ensure(
            Sorted(highestValid).SequenceEqual(Sorted(perSegment[TotalSegments - 1])),
            $"positive control: Segment={Py.Repr(TotalSegments - 1)} with TotalSegments={Py.Repr(TotalSegments)} must be a valid, repeatable request; got {Py.Repr(highestValid.Count)} items vs {Py.Repr(perSegment[TotalSegments - 1].Count)} earlier");
        var err = await ClientError(() => ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            ConsistentRead = true,
            TotalSegments = TotalSegments,
            Segment = TotalSegments,
        }));
        Ensure(
            err.Code == "ValidationException",
            $"Segment must be less than TotalSegments, so Segment={Py.Repr(TotalSegments)} with TotalSegments={Py.Repr(TotalSegments)} must be refused with ValidationException; got {Py.Repr(err.Code)} / {Py.Repr(err.Message)}");
        const string expected = "The Segment parameter is zero-based and must be less than parameter TotalSegments";
        Ensure(err.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(err.Message)}");
        // Effect-absent: the refusal read nothing, so the table is untouched and the valid call still
        // returns the same partition.
        var after = await ScanAll(ddb, table);
        Ensure(
            Sorted(after).SequenceEqual(written),
            $"a refused Scan must not change the table; an unsegmented Scan now returns {Py.Repr(after.Count)} items, expected {Py.Repr(Items)}");
    }
}
