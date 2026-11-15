// Translated from behavioral/dynamodbstreams/check_closed_shard_get_records_ends_with_a_null_next_shard_iterator.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Numerics;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using Streams = Amazon.DynamoDBStreams.Model;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDbStreams;

/// <summary>dynamodbstreams.closed-shard-get-records-ends-with-a-null-next-shard-iterator</summary>
[Category("AwsEvaluationSuite")]
public sealed class ClosedShardGetRecordsEndsWithANullNextShardIterator
{
    private const string CheckId = "closed-shard-get-records-ends-with-a-null-next-shard-iterator";
    private const int Writes = 4;
    private const int FreezeReads = 3;
    private const double ClosedDrainS = 120.0;

    /// <summary>
    /// A closed shard signals end-of-data by omitting NextShardIterator, while the same past-the-end
    /// read on the shard while open returns an empty page with an iterator; a closed shard still
    /// replays all its records in order.
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_streams_GetRecords.html
    /// </summary>
    [Test]
    [DisplayName("dynamodbstreams.closed-shard-get-records-ends-with-a-null-next-shard-iterator")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var streams = ctx.DynamoStreams;

        var (table, streamArn) = await CreateStreamTable(ctx, ddb, StreamViewType.NEW_AND_OLD_IMAGES, "sim2real-behav-dbsnull-");
        var (_, shardIds, horizon) = await WaitStreamUsable(streams, streamArn);

        for (var n = 1; n <= Writes; n++)
        {
            await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S("null-iterator-item")), ("v", N(n.ToString(CultureInfo.InvariantCulture)))),
            });
        }

        var tagged = await Drain(streams, shardIds, horizon, want: Writes);
        Ensure(
            tagged.Count == Writes,
            $"all {Writes} writes must reach the stream before the shard is closed; drained "
            + $"{tagged.Count} records");
        var landed = tagged.Select(t => t.ShardId).ToHashSet(StringComparer.Ordinal);
        Ensure(
            landed.Count == 1,
            "all writes to one partition key must land on ONE shard for the open/closed twin below "
            + $"to compare like with like; they landed on {Py.Repr(landed.Order(StringComparer.Ordinal).ToList())}");
        var shard = landed.Single();
        var seqs = tagged.Select(t => t.Record.Dynamodb.SequenceNumber).ToList();
        var written = tagged.Select(t => t.Record.Dynamodb.NewImage["v"].N).ToList();
        var expected = Enumerable.Range(1, Writes).Select(n => n.ToString(CultureInfo.InvariantCulture)).ToList();
        Ensure(
            written.SequenceEqual(expected, StringComparer.Ordinal),
            $"the shard must replay the writes in order before it is closed; expected {Py.Repr(expected)}, "
            + $"got {Py.Repr(written)}");

        // Open shard: an empty page is not an end, so every past-the-end read still returns an iterator.
        var cursor = (await streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
        {
            StreamArn = streamArn,
            ShardId = shard,
            ShardIteratorType = Amazon.DynamoDBStreams.ShardIteratorType.AFTER_SEQUENCE_NUMBER,
            SequenceNumber = seqs[^1],
        })).ShardIterator;
        for (var attempt = 1; attempt <= FreezeReads; attempt++)
        {
            var resp = await streams.GetRecordsAsync(new Streams.GetRecordsRequest { ShardIterator = cursor });
            Ensure(
                resp.Records is { Count: 0 },
                $"read {attempt} of {FreezeReads} past the end of the OPEN shard {shard} must "
                + "yield no records, got "
                + $"{Py.Repr((resp.Records ?? []).Select(r => r.Dynamodb.NewImage["v"].N).ToList())}");
            Ensure(
                !string.IsNullOrEmpty(resp.NextShardIterator),
                $"read {attempt} of {FreezeReads}: an OPEN shard read past its end must still hand "
                + "back a NextShardIterator -- an empty page is not an end-of-shard signal. Response "
                + $"held {Py.Repr(ResponseKeys(resp))}");
            cursor = resp.NextShardIterator;
        }

        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            StreamSpecification = new StreamSpecification { StreamEnabled = false },
        });
        var disabled = await Poll(
            async () => (await streams.DescribeStreamAsync(new Streams.DescribeStreamRequest { StreamArn = streamArn })).StreamDescription,
            // Non-empty first: all() over an empty shard list is vacuously True.
            d => d.StreamStatus?.Value == "DISABLED"
                && d.Shards is { Count: > 0 }
                && d.Shards.All(s => s.SequenceNumberRange.EndingSequenceNumber is not null),
            timeoutS: 240.0,
            description:
                "an independent DescribeStream reports the stream DISABLED with an "
                + "EndingSequenceNumber on every shard -- the structural barrier this check's "
                + "absence assertions are bounded by");
        var closed = disabled.Shards.ToDictionary(s => s.ShardId, StringComparer.Ordinal);
        Ensure(
            closed.ContainsKey(shard),
            $"the shard that took the writes ({shard}) must survive its own disabling and still be "
            + $"described; DescribeStream now reports {Py.Repr(closed.Keys.Order(StringComparer.Ordinal).ToList())}");
        var ending = closed[shard].SequenceNumberRange.EndingSequenceNumber ?? "";
        Ensure(
            ending.Length > 0 && ending.All(char.IsAsciiDigit),
            $"shard {shard} must be marked CLOSED by a numeric EndingSequenceNumber; "
            + $"SequenceNumberRange is {ReprRange(closed[shard].SequenceNumberRange)}");
        Ensure(
            BigInteger.Parse(ending, CultureInfo.InvariantCulture) >= BigInteger.Parse(seqs[^1], CultureInfo.InvariantCulture),
            $"the shard's EndingSequenceNumber ({ending}) must be at least the sequence number of "
            + $"the last record it accepted ({seqs[^1]}), or the closure excludes records the shard "
            + "still serves");

        // The identical past-the-end read on the closed shard. botocore drops a null member, so the
        // documented null NextShardIterator surfaces as an absent key.
        var closedCursor = (await streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
        {
            StreamArn = streamArn,
            ShardId = shard,
            ShardIteratorType = Amazon.DynamoDBStreams.ShardIteratorType.AFTER_SEQUENCE_NUMBER,
            SequenceNumber = seqs[^1],
        })).ShardIterator;
        var (tailRecords, tailFinal, tailPages) = await DrainUntilNull(
            streams, closedCursor, timeoutS: ClosedDrainS, subject: $"{shard} past its last record");
        Ensure(
            tailRecords.Count == 0,
            "a CLOSED shard read from AFTER its last record must yield nothing, got "
            + $"{Py.Repr(tailRecords.Select(r => r.Dynamodb.Keys).ToList())} over {tailPages} pages");
        Ensure(
            tailFinal.NextShardIterator is null,
            $"the terminal GetRecords page on CLOSED shard {shard} must OMIT NextShardIterator -- "
            + "that null is the only end-of-data signal a consumer has, and the identical call on "
            + $"this same shard while it was OPEN returned one. Response held {Py.Repr(ResponseKeys(tailFinal))} "
            + $"after {tailPages} pages");

        var replayCursor = (await streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
        {
            StreamArn = streamArn,
            ShardId = shard,
            ShardIteratorType = Amazon.DynamoDBStreams.ShardIteratorType.TRIM_HORIZON,
        })).ShardIterator;
        var (fullRecords, fullFinal, fullPages) = await DrainUntilNull(
            streams, replayCursor, timeoutS: ClosedDrainS, subject: $"{shard} from TRIM_HORIZON to exhaustion");
        var replayed = fullRecords.Select(r => r.Dynamodb.NewImage["v"].N).ToList();
        Ensure(
            replayed.SequenceEqual(expected, StringComparer.Ordinal),
            $"draining CLOSED shard {shard} to exhaustion must yield exactly the records it holds, "
            + $"in write order, with no gaps and no duplicates: expected {Py.Repr(expected)}, got "
            + $"{Py.Repr(replayed)} over {fullPages} pages");
        Ensure(
            fullFinal.NextShardIterator is null,
            $"the final page of a full drain of CLOSED shard {shard} must OMIT NextShardIterator; "
            + $"response held {Py.Repr(ResponseKeys(fullFinal))}. A consumer that never sees this null re-reads "
            + "the shard forever and never advances to the child shard");
        var replayedSeqs = fullRecords.Select(r => r.Dynamodb.SequenceNumber).ToList();
        Ensure(
            replayedSeqs.SequenceEqual(seqs, StringComparer.Ordinal),
            "the closed shard must replay the SAME sequence numbers the open shard issued; "
            + $"expected {Py.Repr(seqs)}, got {Py.Repr(replayedSeqs)}");
    }

    /// <summary>The members boto3's GetRecords response dict holds: <c>sorted(resp)</c>.</summary>
    private static List<string> ResponseKeys(Streams.GetRecordsResponse resp)
    {
        var keys = new List<string>();
        if (resp.NextShardIterator is not null)
        {
            keys.Add("NextShardIterator");
        }

        if (resp.Records is not null)
        {
            keys.Add("Records");
        }

        keys.Add("ResponseMetadata");
        return keys;
    }

    private static string ReprRange(Streams.SequenceNumberRange range)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (range.StartingSequenceNumber is not null)
        {
            dict["StartingSequenceNumber"] = range.StartingSequenceNumber;
        }

        if (range.EndingSequenceNumber is not null)
        {
            dict["EndingSequenceNumber"] = range.EndingSequenceNumber;
        }

        return Py.Repr(dict);
    }

    private static string ReprDescription(Streams.StreamDescription d) =>
        $"{{'StreamArn': {Py.Repr(d.StreamArn)}, 'StreamStatus': {Py.Repr(d.StreamStatus?.Value)}, 'Shards': ["
        + string.Join(", ", (d.Shards ?? []).Select(s => $"{{'ShardId': {Py.Repr(s.ShardId)}, 'SequenceNumberRange': {ReprRange(s.SequenceNumberRange)}}}"))
        + "]}";

    /// <summary>Bounded retry that RAISES on timeout; never returns an unsatisfied value.</summary>
    private static async Task<Streams.StreamDescription> Poll(
        Func<Task<Streams.StreamDescription>> fn,
        Func<Streams.StreamDescription, bool> pred,
        double timeoutS,
        double intervalS = 3.0,
        string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        var value = await fn();
        while (!pred(value))
        {
            if (clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw Fail($"{description} not met within {timeoutS}s; last {ReprDescription(value)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            value = await fn();
        }

        return value;
    }

    /// <summary>
    /// Chain GetRecords through empty pages until the response omits NextShardIterator.
    /// Returns (records, final_response, pages); raises at the deadline.
    /// </summary>
    private static async Task<(List<Streams.Record> Records, Streams.GetRecordsResponse Final, int Pages)> DrainUntilNull(
        Amazon.DynamoDBStreams.IAmazonDynamoDBStreams streams,
        string cursor,
        double timeoutS,
        string subject,
        double intervalS = 2.0)
    {
        var records = new List<Streams.Record>();
        var pages = 0;
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var resp = await streams.GetRecordsAsync(new Streams.GetRecordsRequest { ShardIterator = cursor });
            pages++;
            records.AddRange(resp.Records ?? []);
            var nxt = resp.NextShardIterator;
            if (string.IsNullOrEmpty(nxt))
            {
                return (records, resp, pages);
            }

            cursor = nxt;
            if (clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw Fail(
                    $"CLOSED shard {subject} never omitted NextShardIterator: chained {pages} "
                    + $"GetRecords pages over {timeoutS}s and collected {records.Count} records. "
                    + "Without that terminal null a consumer loops on this shard forever and never "
                    + "advances to its child");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }
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
