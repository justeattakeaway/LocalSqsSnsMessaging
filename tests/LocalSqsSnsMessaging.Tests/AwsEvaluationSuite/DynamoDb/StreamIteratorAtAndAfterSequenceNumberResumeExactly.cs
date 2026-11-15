// Translated from behavioral/dynamodb/check_stream_iterator_at_and_after_sequence_number_resume_exactly.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Numerics;
using IAmazonDynamoDBStreams = Amazon.DynamoDBStreams.IAmazonDynamoDBStreams;
using ShardIteratorType = Amazon.DynamoDBStreams.ShardIteratorType;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using Streams = Amazon.DynamoDBStreams.Model;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.stream-iterator-at-and-after-sequence-number-resume-exactly</summary>
[Category("AwsEvaluationSuite")]
public sealed class StreamIteratorAtAndAfterSequenceNumberResumeExactly
{
    private const string CheckId = "stream-iterator-at-and-after-sequence-number-resume-exactly";
    private const string ViewType = "NEW_AND_OLD_IMAGES";
    private const int Mutations = 5;
    private const double DrainBudgetS = 180;
    private const int EmptyPolls = 3;

    private static Dictionary<string, AttributeValue> Key() => Item(("pk", S("checkpoint-subject")));

    /// <summary>Poll probe() until predicate holds. RAISES on timeout -- never returns the last value.</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, double intervalS, string description)
    {
        var clock = FidelityTime.StartNew();
        T last = default!;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not observed within {timeoutS}s; last saw {Py.Repr(last)}");
    }

    private static async Task<List<string>> ShardIds(IAmazonDynamoDBStreams streams, string streamArn)
    {
        var desc = (await streams.DescribeStreamAsync(new Streams.DescribeStreamRequest { StreamArn = streamArn })).StreamDescription;
        return [.. (desc.Shards ?? []).Select(s => s.ShardId)];
    }

    private static async Task<string> Iterator(IAmazonDynamoDBStreams streams, string streamArn, string shardId, string iteratorType, string? sequenceNumber = null)
    {
        var request = new Streams.GetShardIteratorRequest
        {
            StreamArn = streamArn,
            ShardId = shardId,
            ShardIteratorType = new ShardIteratorType(iteratorType),
        };
        if (sequenceNumber is not null)
        {
            request.SequenceNumber = sequenceNumber;
        }

        return (await streams.GetShardIteratorAsync(request)).ShardIterator;
    }

    /// <summary>
    /// Follow one shard's NextShardIterator chain until `want` records are collected.
    /// Raises on timeout or when the chain closes mid-stream.
    /// </summary>
    private static async Task<(List<Streams.Record> Got, string? Cursor)> Collect(
        IAmazonDynamoDBStreams streams, string? iterator, int want, double timeoutS, string description)
    {
        var got = new List<Streams.Record>();
        var cursor = iterator;
        var clock = FidelityTime.StartNew();
        while (got.Count < want)
        {
            if (cursor is null)
            {
                throw Fail($"{description}: the shard handed back a null NextShardIterator after {got.Count} of {want} records, i.e. it closed mid-stream");
            }

            if (clock.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: only {got.Count} of {want} records within {Py.Repr(timeoutS)}s; sequence numbers so far {Py.Repr(Seqs(got))}");
            }

            var resp = await streams.GetRecordsAsync(new Streams.GetRecordsRequest { ShardIterator = cursor });
            got.AddRange(resp.Records ?? []);
            cursor = resp.NextShardIterator;
            if (got.Count < want)
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
            }
        }

        return (got, cursor);
    }

    private static List<string> Seqs(IEnumerable<Streams.Record> records) => [.. records.Select(r => r.Dynamodb.SequenceNumber)];

    private static BigInteger Int(string s) => BigInteger.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>
    /// AT_SEQUENCE_NUMBER includes the checkpointed stream record and AFTER_SEQUENCE_NUMBER excludes
    /// it, so they differ by exactly that record; an iterator past the shard's end returns nothing
    /// until the next mutation, then delivers only that record.
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_streams_GetShardIterator.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.stream-iterator-at-and-after-sequence-number-resume-exactly")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var streams = ctx.DynamoStreams;
        var table = FidelityContext.UniqueName("sim2real-behav-ckpt-");
        var created = await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = new StreamViewType(ViewType) },
            Tags = [new Tag { Key = "sim2real:managed", Value = "true" }, new Tag { Key = "sim2real:check", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);
        var streamArn = created.TableDescription.LatestStreamArn;
        Ensure(!string.IsNullOrEmpty(streamArn), $"CreateTable with StreamEnabled must mint a LatestStreamArn, got {Py.Repr(streamArn)}");
        var shardIds = await PollUntil(
            () => ShardIds(streams, streamArn),
            ids => ids.Count > 0,
            timeoutS: 120,
            intervalS: 3.0,
            description: $"DescribeStream({streamArn}) to report at least one shard");

        // Five mutations to ONE partition key: DynamoDB's ordering guarantee is per item, so these are
        // the only records whose relative order is defined -- and they must share a shard.
        for (var version = 0; version < Mutations; version++)
        {
            var item = Key();
            item["v"] = N(version.ToString(CultureInfo.InvariantCulture));
            await ddb.PutItemAsync(table, item);
        }

        // A fresh stream can have several shards, so the one carrying this key is discovered.
        async Task<string?> FindHistoryShard()
        {
            foreach (var shardId in shardIds)
            {
                var cursor = await Iterator(streams, streamArn, shardId, "TRIM_HORIZON");
                if ((await streams.GetRecordsAsync(new Streams.GetRecordsRequest { ShardIterator = cursor })).Records is { Count: > 0 })
                {
                    return shardId;
                }
            }

            return null;
        }

        var historyShard = (await PollUntil(
            FindHistoryShard,
            s => s is not null,
            timeoutS: DrainBudgetS,
            intervalS: 3.0,
            description: $"one of the shards {Py.Repr(shardIds)} to carry the {Mutations} mutations to {Py.Repr(Key())}; without any delivery no checkpoint claim can be made"))!;

        var (baseline, _) = await Collect(
            streams,
            await Iterator(streams, streamArn, historyShard, "TRIM_HORIZON"),
            want: Mutations,
            timeoutS: DrainBudgetS,
            description: $"TRIM_HORIZON replay of shard {historyShard}");
        Ensure(
            baseline.Count == Mutations,
            $"the shard carrying this key must replay exactly the {Mutations} mutations from the horizon, got {baseline.Count}: {Py.Repr(baseline.Select(r => r.EventName?.Value).ToList())}");
        foreach (var record in baseline)
        {
            Ensure(
                Py.Equal(record.Dynamodb.Keys, Key()),
                $"every record on this shard must be for the single key under test; got {Py.Repr(record.Dynamodb.Keys)} -- the five mutations were spread over shards and nothing below is comparable");
        }

        var baselineSeqs = Seqs(baseline);
        Ensure(
            baselineSeqs.Distinct(StringComparer.Ordinal).Count() == Mutations,
            $"sequence numbers within a shard must be distinct, got {Py.Repr(baselineSeqs)}");
        Ensure(
            baselineSeqs.Select(Int).SequenceEqual(baselineSeqs.Select(Int).Order()),
            $"within one shard, sequence numbers must increase with commit order, got {Py.Repr(baselineSeqs)}");

        var checkpoint = baselineSeqs[2];
        var (atRecords, _) = await Collect(
            streams,
            await Iterator(streams, streamArn, historyShard, "AT_SEQUENCE_NUMBER", checkpoint),
            want: Mutations - 2,
            timeoutS: DrainBudgetS,
            description: $"AT_SEQUENCE_NUMBER({checkpoint}) replay");
        Ensure(
            Seqs(atRecords).SequenceEqual(baselineSeqs[2..]),
            $"AT_SEQUENCE_NUMBER must start reading EXACTLY at the named record, so it must return the checkpointed record and the two after it: expected {Py.Repr(baselineSeqs[2..])}, got {Py.Repr(Seqs(atRecords))}");

        var (afterRecords, _) = await Collect(
            streams,
            await Iterator(streams, streamArn, historyShard, "AFTER_SEQUENCE_NUMBER", checkpoint),
            want: Mutations - 3,
            timeoutS: DrainBudgetS,
            description: $"AFTER_SEQUENCE_NUMBER({checkpoint}) replay");
        Ensure(
            Seqs(afterRecords).SequenceEqual(baselineSeqs[3..]),
            $"AFTER_SEQUENCE_NUMBER must start reading RIGHT AFTER the named record, so the checkpointed record itself must not reappear: expected {Py.Repr(baselineSeqs[3..])}, got {Py.Repr(Seqs(afterRecords))}");

        var difference = Seqs(atRecords).Where(s => !Seqs(afterRecords).Contains(s)).ToList();
        Ensure(
            difference.SequenceEqual([checkpoint]),
            $"the inclusive and exclusive iterators taken at the same sequence number must differ by precisely that one record: difference {Py.Repr(difference)}, expected {Py.Repr(new[] { checkpoint })} (AT={Py.Repr(Seqs(atRecords))}, AFTER={Py.Repr(Seqs(afterRecords))})");

        // Nothing is trimmed within the 24h retention, so the first record is the horizon.
        var (fromFirst, _) = await Collect(
            streams,
            await Iterator(streams, streamArn, historyShard, "AT_SEQUENCE_NUMBER", baselineSeqs[0]),
            want: Mutations,
            timeoutS: DrainBudgetS,
            description: $"AT_SEQUENCE_NUMBER({baselineSeqs[0]}) replay");
        Ensure(
            Seqs(fromFirst).SequenceEqual(baselineSeqs),
            $"AT_SEQUENCE_NUMBER at the shard's first record must reproduce the TRIM_HORIZON replay of an untrimmed shard exactly: expected {Py.Repr(baselineSeqs)}, got {Py.Repr(Seqs(fromFirst))}");

        // The records were committed before this iterator was taken, so it excludes them by position.
        var tailCursor = await Iterator(streams, streamArn, historyShard, "AFTER_SEQUENCE_NUMBER", baselineSeqs[^1]);
        for (var attempt = 0; attempt < EmptyPolls; attempt++)
        {
            var resp = await streams.GetRecordsAsync(new Streams.GetRecordsRequest { ShardIterator = tailCursor });
            Ensure(
                resp.Records is { Count: 0 },
                $"poll {attempt + 1}: an iterator positioned after the last committed record must not replay history; it returned {Py.Repr(Seqs(resp.Records ?? []))}, all of which are already checkpointed ({Py.Repr(baselineSeqs)})");
            Ensure(
                !string.IsNullOrEmpty(resp.NextShardIterator),
                $"poll {attempt + 1}: GetRecords on an OPEN shard must hand back a NextShardIterator even with zero records, or a consumer cannot keep its place; got {Py.Repr(resp.NextShardIterator)}");
            tailCursor = resp.NextShardIterator;
        }

        await ddb.DeleteItemAsync(table, Key());
        var (tailRecords, _) = await Collect(
            streams,
            tailCursor,
            want: 1,
            timeoutS: DrainBudgetS,
            description: "the post-checkpoint REMOVE through the same iterator chain");
        Ensure(
            tailRecords.Count == 1,
            $"the chain must deliver exactly the one mutation committed after the checkpoint, got {tailRecords.Count}: {Py.Repr(Seqs(tailRecords))}");
        var tail = tailRecords[0];
        Ensure(
            tail.EventName?.Value == "REMOVE",
            $"the post-checkpoint mutation was a DeleteItem, so its record must be a REMOVE, got {Py.Repr(tail.EventName?.Value)}");
        Ensure(
            !baselineSeqs.Contains(tail.Dynamodb.SequenceNumber),
            $"the delivered record must be new, not a replay: {Py.Repr(tail.Dynamodb.SequenceNumber)} is one of the already-checkpointed {Py.Repr(baselineSeqs)}");
        Ensure(
            Int(tail.Dynamodb.SequenceNumber) > Int(baselineSeqs[^1]),
            $"within one shard a later mutation must sequence after the earlier ones: {Py.Repr(tail.Dynamodb.SequenceNumber)} vs {Py.Repr(baselineSeqs[^1])}");
        var gone = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = Key(), ConsistentRead = true });
        Ensure(gone.Item is null, $"the REMOVE record must correspond to a real deletion; GetItem still returns {Py.Repr(gone.Item)}");
    }
}
