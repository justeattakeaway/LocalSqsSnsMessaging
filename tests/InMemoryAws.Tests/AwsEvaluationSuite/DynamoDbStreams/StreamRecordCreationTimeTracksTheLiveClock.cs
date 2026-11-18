// Translated from behavioral/dynamodbstreams/check_stream_record_creation_time_tracks_the_live_clock.py
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

/// <summary>dynamodbstreams.stream-record-creation-time-tracks-the-live-clock</summary>
[Category("AwsEvaluationSuite")]
public sealed class StreamRecordCreationTimeTracksTheLiveClock
{
    private const string CheckId = "stream-record-creation-time-tracks-the-live-clock";

    // Forced separation between the two writes. ApproximateCreationDateTime is floored to the second,
    // so two writes inside one second are INDISTINGUISHABLE and a strict-increase assertion over them
    // would be a coin flip. 2.5 s guarantees the floored seconds differ whatever the sub-second phase.
    private const double GapS = 2.5;

    // Tolerance for local-vs-AWS clock skew on the absolute bracket only.
    private const double SkewS = 5.0;

    // Each endpoint floors independently (1 s), plus slack for when the service stamps the record.
    private const double DiffSlackS = 2.0;

    /// <summary>
    /// Each stream record's ApproximateCreationDateTime lies in its write's wall-clock bracket,
    /// is a whole second, and advances between two writes by no more than the measured gap.
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_streams_StreamRecord.html
    /// </summary>
    [Test]
    [DisplayName("dynamodbstreams.stream-record-creation-time-tracks-the-live-clock")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var streams = ctx.DynamoStreams;

        var (table, streamArn) = await CreateStreamTable(ctx, ddb, StreamViewType.NEW_IMAGE, "sim2real-behav-dbsclock-");
        var (_, shardIds, horizon) = await WaitStreamUsable(streams, streamArn);

        var key = Item(("pk", S("clock-item")));

        // --- write A, bracketed by the live clock on both sides ---
        var aLo = Now();
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("clock-item")), ("v", N("1"))) });
        var aHi = Now();
        var first = await Drain(streams, shardIds, horizon, want: 1);
        Ensure(
            first.Count == 1,
            "the first write must produce exactly one stream record before the second write is "
            + $"made, so the two records can be timed independently; drained {first.Count}");
        var (shardA, recA) = first[0];

        // --- force a gap wider than the field's own second-granularity, then write B ---
        await WaitForWallClockGap(since: aHi, gapS: GapS);
        var bLo = Now();
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("clock-item")), ("v", N("2"))) });
        var bHi = Now();
        var measuredGap = bLo - aHi;
        Ensure(
            measuredGap >= GapS,
            $"the forced separation between the two writes must be at least {GapS}s for the "
            + $"strict-increase assertion below to be sound; measured {F3(measuredGap)}s");

        var cursor = (await streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
        {
            StreamArn = streamArn,
            ShardId = shardA,
            ShardIteratorType = Amazon.DynamoDBStreams.ShardIteratorType.AFTER_SEQUENCE_NUMBER,
            SequenceNumber = recA.Dynamodb.SequenceNumber,
        })).ShardIterator;
        var second = await Drain(streams, [shardA], [cursor], want: 1);
        Ensure(
            second.Count == 1,
            $"the second write must arrive on the same shard {shardA} after the first record's "
            + $"sequence number; drained {second.Count}");
        var (_, recB) = second[0];

        // The two records really are the two writes, on one shard, in order.
        Ensure(
            recA.EventName?.Value == "INSERT",
            $"the first write to a fresh key is an INSERT, got {Py.Repr(recA.EventName?.Value)}");
        Ensure(
            recB.EventName?.Value == "MODIFY",
            $"the second write to the same key is a MODIFY, got {Py.Repr(recB.EventName?.Value)}");
        Ensure(
            Py.Equal(recA.Dynamodb.Keys, key) && Py.Equal(recB.Dynamodb.Keys, key),
            $"both records must be for the key under test {Py.Repr(key)}; got "
            + $"{Py.Repr(recA.Dynamodb.Keys)} and {Py.Repr(recB.Dynamodb.Keys)}");
        Ensure(
            BigInteger.Parse(recB.Dynamodb.SequenceNumber, CultureInfo.InvariantCulture)
                > BigInteger.Parse(recA.Dynamodb.SequenceNumber, CultureInfo.InvariantCulture),
            "the later write must carry the greater sequence number on one shard; got "
            + $"{Py.Repr(recA.Dynamodb.SequenceNumber)} then {Py.Repr(recB.Dynamodb.SequenceNumber)}");

        // --- absolute half: each timestamp inside the live bracket of its own write ---
        var epochA = AssertWholeSecondTimestamp(recA);
        var epochB = AssertWholeSecondTimestamp(recB);

        foreach (var (label, epoch, lo, hi) in new[] { ("INSERT", epochA, aLo, aHi), ("MODIFY", epochB, bLo, bHi) })
        {
            // The lower bound is FLOORED because the field is documented as rounded down to the
            // closest second: a write at 10.9 s legitimately reports 10. That floor is required
            // correctness, not slack.
            var floorLo = Math.Floor(lo) - SkewS;
            var ceilHi = hi + SkewS;
            Ensure(
                floorLo <= epoch && epoch <= ceilHi,
                $"the {label} record's ApproximateCreationDateTime must fall inside the live "
                + $"wall-clock bracket of the write that produced it, allowing {SkewS}s of clock "
                + $"skew: expected {F3(floorLo)} <= t <= {F3(ceilHi)} "
                + $"({Iso(floorLo)} .. {Iso(ceilHi)}), got "
                + $"{F3(epoch)} "
                + $"({Iso(epoch)}), which is "
                + $"{(epoch - hi).ToString("+0.000;-0.000", CultureInfo.InvariantCulture)}s from the moment PutItem returned");
        }

        // --- relative half: skew-free, because a constant clock offset cancels in a difference ---
        var diff = epochB - epochA;
        Ensure(
            diff >= 1.0,
            $"the two writes were separated by a measured {F3(measuredGap)}s of wall clock, which "
            + "is more than the field's one-second granularity, so the MODIFY record's timestamp must "
            + "be strictly later than the INSERT record's: expected a difference of at least 1s, got "
            + $"{F3(diff)}s ({F3(epochA)} then {F3(epochB)}). A target that stamps a constant, or "
            + "copies the first record's timestamp forward, lands here");
        Ensure(
            diff <= measuredGap + DiffSlackS,
            "the timestamps must advance by no MORE than the gap this run measured between the two "
            + $"writes, allowing {DiffSlackS}s for the field being floored at each end: expected a "
            + $"difference of at most {F3(measuredGap + DiffSlackS)}s, got {F3(diff)}s. A target "
            + "that stamps the record when it is READ rather than when it is written lands here");
    }

    /// <summary><c>time.time()</c>: the cloud's clock, in epoch seconds.</summary>
    private static double Now() => FidelityTime.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private static string F3(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

    /// <summary><c>datetime.fromtimestamp(t, timezone.utc).isoformat()</c>.</summary>
    private static string Iso(double epoch)
    {
        var at = DateTimeOffset.UnixEpoch.AddTicks((long)Math.Round(epoch * TimeSpan.TicksPerSecond / 10) * 10);
        return at.Ticks % TimeSpan.TicksPerSecond == 0
            ? at.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture)
            : at.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Bounded wait until the wall clock has advanced <paramref name="gapS"/> past <paramref name="since"/>.
    /// Raises on timeout so a frozen clock fails loudly instead of weakening the assertion.
    /// </summary>
    private static async Task WaitForWallClockGap(double since, double gapS, double timeoutS = 60.0)
    {
        var clock = FidelityTime.StartNew();
        while (Now() - since < gapS)
        {
            if (clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw Fail(
                    $"the wall clock did not advance {gapS}s within {timeoutS}s of monotonic time; "
                    + $"it reads {F3(Now())} against a start of {F3(since)}. This check cannot "
                    + "separate two writes without it");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(0.25));
        }
    }

    /// <summary>Assert the record's timestamp shape, then return it as epoch seconds.</summary>
    private static double AssertWholeSecondTimestamp(Streams.Record record)
    {
        var label = record.EventName?.Value;
        var maybe = record.Dynamodb.ApproximateCreationDateTime;
        Ensure(
            maybe is not null,
            $"the {label} record's ApproximateCreationDateTime must deserialise to a datetime; got "
            + "NoneType None");
        var value = maybe.Value;
        Ensure(
            value.Kind != DateTimeKind.Unspecified,
            $"the {label} record's ApproximateCreationDateTime must be timezone-aware so it can be "
            + $"compared to a UTC clock without guessing an offset; got the naive value {value:O}");
        var utc = value.ToUniversalTime();
        var microsecond = utc.Ticks % TimeSpan.TicksPerSecond / 10;
        Ensure(
            microsecond == 0,
            "ApproximateCreationDateTime is documented as rounded down to the closest second, so it "
            + $"must carry no sub-second component; the {label} record reported {utc:O} "
            + $"with microsecond={microsecond}. A target stamping datetime.now() straight "
            + "through lands here");
        return (utc - DateTime.UnixEpoch).TotalSeconds;
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
        Amazon.DynamoDBStreams.IAmazonDynamoDBStreams streams, List<string> shardIds, List<string?> iterators, int want, double timeoutS = 120.0)
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
