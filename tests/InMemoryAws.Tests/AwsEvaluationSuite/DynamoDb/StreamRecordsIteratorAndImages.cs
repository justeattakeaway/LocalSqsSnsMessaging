// Translated from behavioral/dynamodb/check_stream_records_iterator_and_images.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Numerics;
using IAmazonDynamoDBStreams = Amazon.DynamoDBStreams.IAmazonDynamoDBStreams;
using ShardIteratorType = Amazon.DynamoDBStreams.ShardIteratorType;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using Streams = Amazon.DynamoDBStreams.Model;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.stream-records-iterator-and-images</summary>
[Category("AwsEvaluationSuite")]
public sealed class StreamRecordsIteratorAndImages
{
    private const string CheckId = "stream-records-iterator-and-images";
    private const string ViewType = "NEW_AND_OLD_IMAGES";

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS = 2.0, string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (clock.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description} not met within {timeoutS}s. Last: {Py.Repr(val)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            val = await fn();
        }

        return val;
    }

    private static async Task<(Streams.StreamDescription Desc, List<string> ShardIds)> ShardIds(IAmazonDynamoDBStreams streams, string streamArn)
    {
        var desc = (await streams.DescribeStreamAsync(new Streams.DescribeStreamRequest { StreamArn = streamArn })).StreamDescription;
        return (desc, [.. (desc.Shards ?? []).Select(s => s.ShardId)]);
    }

    /// <summary>One shard iterator per shard, of the requested type.</summary>
    private static async Task<List<string?>> Iterators(IAmazonDynamoDBStreams streams, string streamArn, List<string> shardIds, string iteratorType)
    {
        var iterators = new List<string?>();
        foreach (var sid in shardIds)
        {
            iterators.Add((await streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
            {
                StreamArn = streamArn,
                ShardId = sid,
                ShardIteratorType = new ShardIteratorType(iteratorType),
            })).ShardIterator);
        }

        return iterators;
    }

    /// <summary>
    /// Follow every shard's NextShardIterator chain until `want` records have been collected or
    /// the deadline passes.
    /// Returns (tagged, survivingIterators) where `tagged` is a list of (shardId, record) in
    /// per-shard arrival order -- GetRecords returns a shard's records in sequence order, and that is
    /// the ONLY order the service defines.
    /// A freshly enabled stream on an empty PAY_PER_REQUEST table can already have several shards,
    /// with mutations distributed across them by partition key, and sequence numbers are per-shard:
    /// records are kept shard-tagged so ordering is only asserted within a shard.
    /// </summary>
    private static async Task<(List<(string ShardId, Streams.Record Record)> Tagged, List<string?> Cursors)> Drain(
        IAmazonDynamoDBStreams streams, List<string> shardIds, List<string?> iterators, int want, double timeoutS)
    {
        var tagged = new List<(string, Streams.Record)>();
        var cursors = new List<string?>(iterators);
        var clock = FidelityTime.StartNew();
        while (tagged.Count < want && clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
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
                    tagged.AddRange(got.Select(r => (shardIds[i], r)));
                    progressed = true;
                }

                // A shard that is still OPEN must always hand back a NextShardIterator, even when
                // it returned zero records -- that is how a consumer keeps its place.
                cursors[i] = resp.NextShardIterator;
            }

            if (tagged.Count >= want)
            {
                break;
            }

            if (!progressed)
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
            }
        }

        return (tagged, cursors);
    }

    private static BigInteger Int(string s) => BigInteger.Parse(s, CultureInfo.InvariantCulture);

    private static Dictionary<string, AttributeValue> Key() => Item(("pk", S("s1")));

    private static Dictionary<string, AttributeValue> KeyWith(string v)
    {
        var item = Key();
        item["v"] = N(v);
        return item;
    }

    /// <summary>
    /// TRIM_HORIZON replays every mutation as one record in per-shard commit order with the right
    /// eventName, Keys and NEW_AND_OLD_IMAGES images, open shards always return a NextShardIterator,
    /// and a LATEST iterator taken after those mutations sees none of them but sees a later one.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.stream-records-iterator-and-images")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var streams = ctx.DynamoStreams;
        var t = FidelityContext.UniqueName("sim2real-behav-");
        var created = await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = t,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = new StreamViewType(ViewType) },
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(t));
        await ddb.WaitForTableExists(t, delaySeconds: 2, maxAttempts: 250);
        var streamArn = created.TableDescription.LatestStreamArn;
        Ensure(
            !string.IsNullOrEmpty(streamArn) && streamArn.Contains(":table/", StringComparison.Ordinal) && streamArn.Contains("/stream/", StringComparison.Ordinal),
            $"CreateTable with StreamEnabled must mint a LatestStreamArn of the form arn:...:table/<name>/stream/<label>, got {Py.Repr(streamArn)}");
        var describedTable = (await ddb.DescribeTableAsync(t)).Table;
        Ensure(
            describedTable.LatestStreamArn == streamArn,
            $"DescribeTable must echo the same LatestStreamArn, got {describedTable.LatestStreamArn} vs {streamArn}");
        var spec = describedTable.StreamSpecification;
        Ensure(
            spec is not null && spec.StreamEnabled == true && spec.StreamViewType?.Value == ViewType,
            $"DescribeTable must echo the stream specification, got {{'StreamEnabled': {Py.Repr(spec?.StreamEnabled)}, 'StreamViewType': {Py.Repr(spec?.StreamViewType?.Value)}}}");

        // --- ListStreams surfaces the stream for this table ---
        var listed = (await streams.ListStreamsAsync(new Streams.ListStreamsRequest { TableName = t })).Streams ?? [];
        Ensure(
            listed.Any(s => s.StreamArn == streamArn),
            $"ListStreams(TableName={t}) must include {streamArn}, got {Py.Repr(listed.Select(s => s.StreamArn).ToList())}");
        var entry = listed.First(s => s.StreamArn == streamArn);
        Ensure(entry.TableName == t, $"the ListStreams entry must name the table, got {Py.Repr(entry.TableName)}");

        // --- DescribeStream exposes the stream's shape and at least one shard ---
        var (desc, shardIds) = await Poll(
            () => ShardIds(streams, streamArn),
            r => r.ShardIds.Count > 0,
            timeoutS: 120,
            description: "DescribeStream reports at least one shard");
        Ensure(
            desc.StreamStatus?.Value is "ENABLING" or "ENABLED",
            $"a freshly enabled stream must be ENABLING or ENABLED, got {desc.StreamStatus?.Value}");
        Ensure(desc.StreamViewType?.Value == ViewType, $"StreamViewType must be {ViewType}, got {desc.StreamViewType?.Value}");
        Ensure(desc.StreamArn == streamArn, $"StreamArn mismatch: {desc.StreamArn}");
        Ensure(desc.TableName == t, $"DescribeStream must name the table, got {desc.TableName}");
        Ensure(
            desc.KeySchema is { Count: 1 } && desc.KeySchema[0].AttributeName == "pk" && desc.KeySchema[0].KeyType?.Value == "HASH",
            $"DescribeStream must echo the table key schema, got {Py.Repr(desc.KeySchema?.Select(k => new Dictionary<string, string?> { ["AttributeName"] = k.AttributeName, ["KeyType"] = k.KeyType?.Value }).ToList())}");

        // --- three mutations of one item: INSERT, MODIFY, REMOVE ---
        await ddb.PutItemAsync(t, KeyWith("1"));
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = t,
            Key = Key(),
            UpdateExpression = "SET v = :v",
            ExpressionAttributeValues = Item((":v", N("2"))),
        });
        await ddb.DeleteItemAsync(t, Key());

        // --- TRIM_HORIZON replays all three, in commit order ---
        var horizon = await Iterators(streams, streamArn, shardIds, "TRIM_HORIZON");
        var (tagged, _) = await Drain(streams, shardIds, horizon, want: 3, timeoutS: 180);
        var records = tagged.Select(x => x.Record).ToList();
        Ensure(
            records.Count == 3,
            $"TRIM_HORIZON must replay exactly the 3 mutations from the start of the shard, got {records.Count}: {Py.Repr(records.Select(r => r.EventName?.Value).ToList())}");
        // All three mutations touch ONE item, and DynamoDB's ordering guarantee is per partition key:
        // they must therefore all be delivered on the SAME shard. This is what makes the commit-order
        // assertion below meaningful -- across shards there is no defined order at all -- and it is a
        // real constraint on a multi-shard stream.
        var historyShards = tagged.Select(x => x.ShardId).ToHashSet(StringComparer.Ordinal);
        Ensure(
            historyShards.Count == 1,
            $"every mutation to a single partition key must land on one shard, so that a consumer sees them in order; the 3 mutations to s1 were spread over {Py.Repr(historyShards.Order(StringComparer.Ordinal).ToList())}");
        var historyShard = historyShards.Single();
        Ensure(
            records.Select(r => r.EventName?.Value).SequenceEqual(["INSERT", "MODIFY", "REMOVE"]),
            $"one record per mutation, in commit order, with the mutation-specific eventName; got {Py.Repr(records.Select(r => r.EventName?.Value).ToList())}");
        foreach (var r in records)
        {
            Ensure(r.EventSource == "aws:dynamodb", $"eventSource must be aws:dynamodb, got {Py.Repr(r.EventSource)}");
            Ensure(
                Py.Equal(r.Dynamodb.Keys, Key()),
                $"every stream record must carry the item Keys regardless of view type, got {Py.Repr(r.Dynamodb.Keys)}");
            Ensure(
                r.Dynamodb.StreamViewType?.Value == ViewType,
                $"each record must report its StreamViewType, got {r.Dynamodb.StreamViewType?.Value}");
            Ensure(
                r.Dynamodb.ApproximateCreationDateTime is not null,
                "each record must carry ApproximateCreationDateTime");
        }

        var (insert, modify, remove) = (records[0], records[1], records[2]);
        Ensure(Py.Equal(insert.Dynamodb.NewImage, KeyWith("1")), $"INSERT must carry the full NewImage, got {Py.Repr(insert.Dynamodb.NewImage)}");
        Ensure(insert.Dynamodb.OldImage is null, $"INSERT has no prior image, so OldImage must be absent, got {Py.Repr(insert.Dynamodb.OldImage)}");
        Ensure(Py.Equal(modify.Dynamodb.OldImage, KeyWith("1")), $"MODIFY must carry the pre-update OldImage, got {Py.Repr(modify.Dynamodb.OldImage)}");
        Ensure(Py.Equal(modify.Dynamodb.NewImage, KeyWith("2")), $"MODIFY must carry the post-update NewImage, got {Py.Repr(modify.Dynamodb.NewImage)}");
        Ensure(Py.Equal(remove.Dynamodb.OldImage, KeyWith("2")), $"REMOVE must carry the deleted item as OldImage, got {Py.Repr(remove.Dynamodb.OldImage)}");
        Ensure(remove.Dynamodb.NewImage is null, $"REMOVE has no resulting image, so NewImage must be absent, got {Py.Repr(remove.Dynamodb.NewImage)}");

        // Within one shard -- established above -- sequence numbers are ordered and comparable.
        var seqs = records.Select(r => Int(r.Dynamodb.SequenceNumber)).ToList();
        Ensure(
            seqs.SequenceEqual(seqs.Order()) && seqs.Distinct().Count() == 3,
            $"SequenceNumbers must be distinct and monotonically increasing with commit order, got [{string.Join(", ", seqs)}]");
        var shardStart = desc.Shards.ToDictionary(s => s.ShardId, s => Int(s.SequenceNumberRange.StartingSequenceNumber), StringComparer.Ordinal);
        Ensure(
            seqs[0] > shardStart[historyShard],
            $"a record must sequence after its own shard's StartingSequenceNumber ({shardStart[historyShard]}), got {seqs[0]}");

        // --- LATEST must NOT replay history ---
        var latest = await Iterators(streams, streamArn, shardIds, "LATEST");
        var firstPoll = new List<Streams.GetRecordsResponse>();
        foreach (var it in latest)
        {
            firstPoll.Add(await streams.GetRecordsAsync(new Streams.GetRecordsRequest { ShardIterator = it }));
        }

        foreach (var resp in firstPoll)
        {
            Ensure(
                resp.Records is { Count: 0 },
                $"a LATEST iterator taken after the mutations must see none of them; got {Py.Repr((resp.Records ?? []).Select(r => r.EventName?.Value).ToList())}");
            Ensure(
                !string.IsNullOrEmpty(resp.NextShardIterator),
                "GetRecords on an open shard must return a NextShardIterator even with zero records");
        }

        var cursors = firstPoll.Select(resp => (string?)resp.NextShardIterator).ToList();

        // --- but it DOES see a mutation made after it was taken ---
        // A DIFFERENT partition key, so this also measures the fan-out: the record may be delivered on
        // any shard, and the LATEST iterators are followed on all of them.
        await ddb.PutItemAsync(t, Item(("pk", S("s2")), ("v", N("9"))));
        (var tail, cursors) = await Drain(streams, shardIds, cursors, want: 1, timeoutS: 180);
        Ensure(
            tail.Count == 1,
            $"the LATEST-anchored iterator must deliver exactly the one post-anchor mutation, got {Py.Repr(tail.Select(x => x.Record.EventName?.Value).ToList())}");
        var (tailShard, tailRecord) = tail[0];
        Ensure(tailRecord.EventName?.Value == "INSERT", Py.Repr(tailRecord.EventName?.Value));
        Ensure(
            Py.Equal(tailRecord.Dynamodb.Keys, Item(("pk", S("s2")))),
            $"the LATEST iterator must not replay the s1 history, got {Py.Repr(tailRecord.Dynamodb.Keys)}");
        // Deliberately NOT compared against seqs[-1]: this record may be on another shard, and sequence
        // numbers are only ordered within a shard. It is compared against its OWN shard's start, which
        // is the strongest statement the service's contract supports.
        Ensure(
            Int(tailRecord.Dynamodb.SequenceNumber) > shardStart[tailShard],
            $"the post-anchor record must sequence after its own shard's start ({shardStart[tailShard]}), got {tailRecord.Dynamodb.SequenceNumber}");

        // Ordering is guaranteed only within a shard: another mutation to the ORIGINAL key must arrive on the SAME shard as the
        // replayed history and must sequence after all of it.
        await ddb.PutItemAsync(t, KeyWith("7"));
        var (resumed, _) = await Drain(streams, shardIds, cursors, want: 1, timeoutS: 180);
        Ensure(
            resumed.Count == 1,
            $"a further mutation to the original key must be delivered exactly once, got {Py.Repr(resumed.Select(x => (x.ShardId, x.Record.EventName?.Value)).ToList())}");
        var (resumedShard, resumedRecord) = resumed[0];
        Ensure(
            resumedShard == historyShard,
            $"a mutation to the same partition key must stay on the shard that carried its history, got {resumedShard} vs {historyShard}");
        Ensure(Py.Equal(resumedRecord.Dynamodb.Keys, Key()), $"expected the s1 record, got {Py.Repr(resumedRecord.Dynamodb.Keys)}");
        Ensure(
            Int(resumedRecord.Dynamodb.SequenceNumber) > seqs[^1],
            $"within a shard, a later mutation must sequence after the earlier ones, got {resumedRecord.Dynamodb.SequenceNumber} vs {seqs[^1]}");
    }
}
