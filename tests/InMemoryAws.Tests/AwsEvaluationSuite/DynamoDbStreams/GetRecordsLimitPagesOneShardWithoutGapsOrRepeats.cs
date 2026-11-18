// Translated from behavioral/dynamodbstreams/check_get_records_limit_pages_one_shard_without_gaps_or_repeats.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Numerics;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using Streams = Amazon.DynamoDBStreams.Model;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDbStreams;

/// <summary>dynamodbstreams.get-records-limit-pages-one-shard-without-gaps-or-repeats</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetRecordsLimitPagesOneShardWithoutGapsOrRepeats
{
    private const string CheckId = "get-records-limit-pages-one-shard-without-gaps-or-repeats";
    private const int Writes = 12;
    private const int Limit = 3;

    /// <summary>
    /// GetRecords(Limit=3) over a shard holding 12 records caps every page at 3 and, chained to
    /// exhaustion, reproduces the unpaginated read exactly: same records, order and sequence numbers.
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_streams_GetRecords.html
    /// </summary>
    [Test]
    [DisplayName("dynamodbstreams.get-records-limit-pages-one-shard-without-gaps-or-repeats")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var streams = ctx.DynamoStreams;

        var (table, streamArn) = await CreateStreamTable(ctx, ddb, StreamViewType.NEW_IMAGE, "sim2real-behav-dbslimit-");
        var (_, shardIds, horizon) = await WaitStreamUsable(streams, streamArn);

        for (var n = 1; n <= Writes; n++)
        {
            await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S("limit-item")), ("v", N(n.ToString(CultureInfo.InvariantCulture)))),
            });
        }

        // --- positive control: the unpaginated read, on the same shard, in the same run ---
        var tagged = await Drain(streams, shardIds, horizon, want: Writes);
        Ensure(
            tagged.Count == Writes,
            $"the unpaginated control must see all {Writes} writes before the paged read is "
            + $"compared against it; drained {tagged.Count}");
        var landed = tagged.Select(t => t.ShardId).ToHashSet(StringComparer.Ordinal);
        Ensure(
            landed.Count == 1,
            "all writes to one partition key must land on ONE shard, so the paging surface under "
            + $"test is a single iterator chain; they landed on {Py.Repr(landed.Order(StringComparer.Ordinal).ToList())}");
        var shard = landed.Single();
        var expectedValues = Enumerable.Range(1, Writes).Select(n => n.ToString(CultureInfo.InvariantCulture)).ToList();
        var baselineValues = tagged.Select(t => t.Record.Dynamodb.NewImage["v"].N).ToList();
        var baselineSeqs = tagged.Select(t => t.Record.Dynamodb.SequenceNumber).ToList();
        Ensure(
            baselineValues.SequenceEqual(expectedValues, StringComparer.Ordinal),
            $"the unpaginated control must replay the writes in order; expected {Py.Repr(expectedValues)}, "
            + $"got {Py.Repr(baselineValues)}");

        // --- the subject: the same shard, read again from TRIM_HORIZON, capped at Limit per page ---
        var cursor = (await streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
        {
            StreamArn = streamArn,
            ShardId = shard,
            ShardIteratorType = Amazon.DynamoDBStreams.ShardIteratorType.TRIM_HORIZON,
        })).ShardIterator;
        var (paged, pageSizes) = await DrainPaged(streams, cursor, limit: Limit, want: Writes, shard: shard);

        var oversized = pageSizes.Select((n, i) => (I: i, N: n)).Where(p => p.N > Limit).ToList();
        Ensure(
            oversized.Count == 0,
            $"GetRecords(Limit={Limit}) must never return more than {Limit} records in one page; "
            + $"pages {Py.Repr(oversized.Select(p => p.I).ToList())} returned {Py.Repr(oversized.Select(p => p.N).ToList())} records "
            + $"respectively (all page sizes in order: {Py.Repr(pageSizes)}). A target that ignores Limit "
            + "hands the consumer a batch it never asked for");

        var nonempty = pageSizes.Where(n => n != 0).ToList();
        var leastPages = (int)Math.Ceiling(Writes / (double)Limit);
        Ensure(
            nonempty.Count >= leastPages,
            $"reading {Writes} records {Limit} at a time needs at least "
            + $"ceil({Writes}/{Limit})={leastPages} non-empty pages; the read used "
            + $"{nonempty.Count} (sizes {Py.Repr(nonempty)})");
        Ensure(
            nonempty.Sum() == Writes,
            $"the paged pages must account for exactly {Writes} records with none dropped and none "
            + $"duplicated; they sum to {nonempty.Sum()} (sizes {Py.Repr(nonempty)})");
        Ensure(
            nonempty.Max() == Limit,
            $"with all {Writes} records already durable in shard {shard}, at least one page must be "
            + $"FULL at the requested Limit of {Limit}; the largest page held {nonempty.Max()} "
            + $"(sizes {Py.Repr(nonempty)}). A target that serves one record per page regardless of Limit "
            + "lands here");

        var pagedValues = paged.Select(rec => rec.Dynamodb.NewImage["v"].N).ToList();
        var pagedSeqs = paged.Select(rec => rec.Dynamodb.SequenceNumber).ToList();
        Ensure(
            pagedValues.SequenceEqual(baselineValues, StringComparer.Ordinal),
            "paging must not change WHAT is read or the order it is read in: the unpaginated "
            + $"control yielded {Py.Repr(baselineValues)}, the Limit={Limit} read yielded {Py.Repr(pagedValues)} "
            + $"over pages {Py.Repr(pageSizes)}");
        Ensure(
            pagedSeqs.SequenceEqual(baselineSeqs, StringComparer.Ordinal),
            "paging must return the SAME sequence numbers, in the same order, as the unpaginated "
            + $"read: expected {Py.Repr(baselineSeqs)}, got {Py.Repr(pagedSeqs)}");
        var distinct = pagedSeqs.Distinct(StringComparer.Ordinal).Count();
        Ensure(
            distinct == Writes,
            $"no record may be served twice across page boundaries: {Writes} records yielded "
            + $"{distinct} distinct sequence numbers ({Py.Repr(pagedSeqs)})");
        var ascending = pagedSeqs.Select(s => BigInteger.Parse(s, CultureInfo.InvariantCulture)).ToList();
        Ensure(
            ascending.Zip(ascending.Skip(1)).All(p => p.Second > p.First),
            "sequence numbers must increase strictly across the whole paged read, including at the "
            + $"page boundaries where a target is most likely to reorder or rewind; got {Py.Repr(pagedSeqs)} "
            + $"over pages {Py.Repr(pageSizes)}");
    }

    /// <summary>
    /// Chain GetRecords(Limit=limit) until <paramref name="want"/> records arrive; return (records, page_sizes).
    /// Empty pages are legal mid-shard, so they are chained through. Raises on a short chain.
    /// </summary>
    private static async Task<(List<Streams.Record> Records, List<int> PageSizes)> DrainPaged(
        Amazon.DynamoDBStreams.IAmazonDynamoDBStreams streams,
        string cursor,
        int limit,
        int want,
        string shard,
        double timeoutS = 150.0,
        double intervalS = 2.0)
    {
        var records = new List<Streams.Record>();
        var pageSizes = new List<int>();
        var clock = FidelityTime.StartNew();
        while (records.Count < want)
        {
            var resp = await streams.GetRecordsAsync(new Streams.GetRecordsRequest { ShardIterator = cursor, Limit = limit });
            var got = resp.Records ?? [];
            pageSizes.Add(got.Count);
            records.AddRange(got);
            var nxt = resp.NextShardIterator;
            if (records.Count >= want)
            {
                break;
            }

            if (string.IsNullOrEmpty(nxt))
            {
                throw Fail(
                    $"the Limit={limit} iterator chain on OPEN shard {shard} ended after "
                    + $"{pageSizes.Count} pages (sizes {Py.Repr(pageSizes)}) holding only {records.Count} of "
                    + $"{want} records; an open shard must keep handing back a NextShardIterator");
            }

            cursor = nxt;
            if (clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw Fail(
                    $"the Limit={limit} read of shard {shard} collected {records.Count} of {want} "
                    + $"records over {pageSizes.Count} pages in {timeoutS}s (sizes {Py.Repr(pageSizes)})");
            }

            if (got.Count == 0)
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            }
        }

        return (records, pageSizes);
    }

    private static async Task<(string Name, string StreamArn)> CreateStreamTable(
        FidelityContext ctx, IAmazonDynamoDB ddb, StreamViewType viewType, string prefix)
    {
        var name = FidelityContext.UniqueName(prefix);
        var created = await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = name,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = viewType },
            Tags = [new Tag { Key = "sim2real-check", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(name));
        var streamArn = created.TableDescription.LatestStreamArn;
        Ensure(
            !string.IsNullOrEmpty(streamArn),
            "CreateTable with StreamEnabled must mint a LatestStreamArn, got "
            + $"{Py.Repr(created.TableDescription.LatestStreamArn)}");
        return (name, streamArn);
    }

    /// <summary>
    /// Poll until DescribeStream lists shards AND every shard hands back an iterator.
    /// GetShardIterator can answer ResourceNotFoundException for a few seconds after ENABLED.
    /// </summary>
    private static async Task<(Streams.StreamDescription Desc, List<string> ShardIds, List<string?> Iterators)> WaitStreamUsable(
        Amazon.DynamoDBStreams.IAmazonDynamoDBStreams streams, string streamArn, double timeoutS = 180.0)
    {
        var clock = FidelityTime.StartNew();
        string? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            var desc = (await streams.DescribeStreamAsync(new Streams.DescribeStreamRequest { StreamArn = streamArn })).StreamDescription;
            var shardIds = (desc.Shards ?? []).Select(s => s.ShardId).ToList();
            if (shardIds.Count > 0)
            {
                try
                {
                    var iterators = new List<string?>();
                    foreach (var sid in shardIds)
                    {
                        iterators.Add((await streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
                        {
                            StreamArn = streamArn,
                            ShardId = sid,
                            ShardIteratorType = Amazon.DynamoDBStreams.ShardIteratorType.TRIM_HORIZON,
                        })).ShardIterator);
                    }

                    return (desc, shardIds, iterators);
                }
                catch (AmazonServiceException exc)
                {
                    last = exc.ErrorCode;
                    if (last != "ResourceNotFoundException")
                    {
                        throw;
                    }
                }
            }
            else
            {
                last = $"{desc.StreamStatus?.Value} with 0 shards";
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        throw Fail($"stream {streamArn} never became readable within {timeoutS}s; last state {Py.Repr(last)}");
    }

    /// <summary>Follow every shard's NextShardIterator chain until <paramref name="want"/> records are collected.</summary>
    private static async Task<List<(string ShardId, Streams.Record Record)>> Drain(
        Amazon.DynamoDBStreams.IAmazonDynamoDBStreams streams, List<string> shardIds, List<string?> iterators, int want, double timeoutS = 150.0)
    {
        var tagged = new List<(string ShardId, Streams.Record Record)>();
        var cursors = iterators.ToList();
        var clock = FidelityTime.StartNew();
        while (tagged.Count < want && clock.Elapsed.TotalSeconds < timeoutS)
        {
            var progressed = false;
            for (var i = 0; i < cursors.Count; i++)
            {
                var cursor = cursors[i];
                if (cursor is null)
                {
                    continue;
                }

                var resp = await streams.GetRecordsAsync(new Streams.GetRecordsRequest { ShardIterator = cursor });
                var got = resp.Records ?? [];
                if (got.Count > 0)
                {
                    tagged.AddRange(got.Select(rec => (shardIds[i], rec)));
                    progressed = true;
                }

                cursors[i] = resp.NextShardIterator;
            }

            if (tagged.Count >= want || !cursors.Any(c => !string.IsNullOrEmpty(c)))
            {
                break;
            }

            if (!progressed)
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
            }
        }

        return tagged;
    }
}
