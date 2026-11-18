// Translated from behavioral/dynamodbstreams/check_concurrent_writes_each_appear_exactly_once.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using Streams = Amazon.DynamoDBStreams.Model;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDbStreams;

/// <summary>dynamodbstreams.concurrent-writes-each-appear-exactly-once</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentWritesEachAppearExactlyOnce
{
    private const string CheckId = "concurrent-writes-each-appear-exactly-once";
    private const int Keys = 16;

    /// <summary>
    /// Sixteen simultaneous writes to sixteen distinct keys each appear in the stream exactly once:
    /// draining every shard yields sixteen INSERT records for precisely the written keys, with distinct
    /// eventIDs and SequenceNumbers (no loss, no duplication).
    /// </summary>
    [Test]
    [DisplayName("dynamodbstreams.concurrent-writes-each-appear-exactly-once")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var streams = ctx.DynamoStreams;

        var (table, streamArn) = await CreateStreamTable(ctx, ddb, StreamViewType.NEW_IMAGE, "sim2real-behav-dbsconc-");
        var (_, shardIds, horizon) = await WaitStreamUsable(streams, streamArn);

        var written = Enumerable.Range(0, Keys).Select(i => $"conc-{i.ToString("D2", CultureInfo.InvariantCulture)}").ToList();

        async Task Put(string name) =>
            await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S(name)), ("v", S(name))) });

        using (ctx.Concurrently())
        {
            await Task.WhenAll(written.Select(name => Task.Run(() => Put(name), cancellationToken)));
        }

        var tagged = await Drain(streams, shardIds, horizon, want: Keys);
        var records = tagged.Select(t => t.Record).ToList();
        Ensure(
            records.Count == Keys,
            $"every one of the {Keys} concurrent writes must be delivered; drained "
            + $"{records.Count} records for keys "
            + $"{Py.Repr(records.Select(r => r.Dynamodb.Keys["pk"].S).Order(StringComparer.Ordinal).ToList())}");

        var delivered = records.Select(rec => rec.Dynamodb.Keys["pk"].S).ToList();
        var sortedDelivered = delivered.Order(StringComparer.Ordinal).ToList();
        var sortedWritten = written.Order(StringComparer.Ordinal).ToList();
        Ensure(
            sortedDelivered.SequenceEqual(sortedWritten, StringComparer.Ordinal),
            "the delivered key multiset must equal the written one exactly -- no loss, no "
            + $"duplication, nothing invented; wrote {Py.Repr(sortedWritten)}, got {Py.Repr(sortedDelivered)}");
        var names = records.Select(rec => rec.EventName.Value).ToHashSet(StringComparer.Ordinal);
        Ensure(
            names.SetEquals(["INSERT"]),
            $"a first write to each key is an INSERT; got event names {Py.Repr(names.Order(StringComparer.Ordinal).ToList())}");
        var eventIds = records.Select(rec => rec.EventID).ToList();
        var distinctIds = eventIds.Distinct(StringComparer.Ordinal).Count();
        Ensure(
            distinctIds == Keys,
            $"each record must carry a distinct eventID; got {distinctIds} distinct of {Keys}");
        var seqs = records.Select(rec => rec.Dynamodb.SequenceNumber).ToList();
        var distinctSeqs = seqs.Distinct(StringComparer.Ordinal).Count();
        Ensure(
            distinctSeqs == Keys,
            $"each record must carry a distinct SequenceNumber; got {distinctSeqs} distinct of "
            + $"{Keys}: {Py.Repr(seqs.Order(StringComparer.Ordinal).ToList())}");
        foreach (var rec in records)
        {
            var body = rec.Dynamodb.NewImage;
            Ensure(
                body["v"].S == body["pk"].S,
                $"each record's NewImage must be the item its own writer wrote, got {Py.Repr(body)}");
        }
    }

    // ===== helpers =====

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
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
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
    /// Poll until DescribeStream reports shards AND every shard hands back an iterator.
    /// Shards appear ~8 s after CreateTable and DescribeStream already reports
    /// StreamStatus=ENABLED, but GetShardIterator keeps answering ResourceNotFoundException
    /// 'Stream does not exist' for a further ~2 s.
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

    /// <summary>
    /// Follow every shard's NextShardIterator chain until <paramref name="want"/> records are collected.
    /// Keeps polling for a few rounds after reaching <paramref name="want"/> would be wrong here -- an
    /// extra duplicate is exactly what the caller asserts against -- so the caller drains to
    /// <paramref name="want"/> and then compares the multiset, which catches a duplicate as a missing key.
    /// </summary>
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
