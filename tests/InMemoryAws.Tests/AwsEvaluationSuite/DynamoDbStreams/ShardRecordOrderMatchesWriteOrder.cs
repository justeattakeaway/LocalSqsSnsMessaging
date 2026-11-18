// Translated from behavioral/dynamodbstreams/check_shard_record_order_matches_write_order.py
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

/// <summary>dynamodbstreams.shard-record-order-matches-write-order</summary>
[Category("AwsEvaluationSuite")]
public sealed class ShardRecordOrderMatchesWriteOrder
{
    private const string CheckId = "shard-record-order-matches-write-order";
    private const int Writes = 12;

    /// <summary>
    /// Twelve successive writes to one partition key land on one shard and are replayed by GetRecords
    /// in commit order with strictly increasing SequenceNumbers, and a second TRIM_HORIZON pass replays
    /// the identical order.
    /// </summary>
    [Test]
    [DisplayName("dynamodbstreams.shard-record-order-matches-write-order")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var streams = ctx.DynamoStreams;

        var (table, streamArn) = await CreateStreamTable(ctx, ddb, StreamViewType.NEW_IMAGE, "sim2real-behav-dbsord-");
        var (_, shardIds, horizon) = await WaitStreamUsable(streams, streamArn);

        for (var n = 1; n <= Writes; n++)
        {
            await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S("ordered-item")), ("v", N(n.ToString(CultureInfo.InvariantCulture)))),
            });
        }

        var tagged = await Drain(streams, shardIds, horizon, want: Writes);
        Ensure(
            tagged.Count == Writes,
            $"TRIM_HORIZON must replay all {Writes} writes to a single key; got {tagged.Count}: "
            + $"{Py.Repr(tagged.Select(t => t.Record.Dynamodb.NewImage is { } image && image.TryGetValue("v", out var v) ? v.N : null).ToList())}");

        var landed = tagged.Select(t => t.ShardId).ToHashSet(StringComparer.Ordinal);
        Ensure(
            landed.Count == 1,
            "every write to one partition key must land on ONE shard so a consumer sees them in "
            + $"order; the {Writes} writes were spread over {Py.Repr(landed.Order(StringComparer.Ordinal).ToList())}");

        var records = tagged.Select(t => t.Record).ToList();
        var values = records.Select(rec => rec.Dynamodb.NewImage["v"].N).ToList();
        var expectedValues = Enumerable.Range(1, Writes).Select(n => n.ToString(CultureInfo.InvariantCulture)).ToList();
        Ensure(
            values.SequenceEqual(expectedValues, StringComparer.Ordinal),
            $"records must arrive in commit order 1..{Writes}; got {Py.Repr(values)}");
        var names = records.Select(rec => rec.EventName?.Value).ToList();
        var expectedNames = new[] { "INSERT" }.Concat(Enumerable.Repeat("MODIFY", Writes - 1)).ToList();
        Ensure(
            names.SequenceEqual(expectedNames),
            $"the first write to a new key is INSERT and every overwrite is MODIFY; got {Py.Repr(names)}");

        var seqs = records.Select(rec => BigInteger.Parse(rec.Dynamodb.SequenceNumber, CultureInfo.InvariantCulture)).ToList();
        Ensure(
            seqs.SequenceEqual(seqs.Order()) && seqs.Distinct().Count() == Writes,
            "SequenceNumbers within a shard must be distinct and strictly increasing with commit "
            + $"order; got {Py.Repr(seqs)}");
        var eventIds = records.Select(rec => rec.EventID).ToList();
        var distinctIds = eventIds.Distinct(StringComparer.Ordinal).Count();
        Ensure(
            distinctIds == Writes,
            $"each record must carry a distinct eventID; got {distinctIds} distinct of "
            + $"{Writes}: {Py.Repr(eventIds)}");

        // Re-read the SAME shard from the horizon: the replay must be stable, not merely sorted once.
        var replayShard = landed.Single();
        var second = await Drain(
            streams,
            [replayShard],
            [(await streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
            {
                StreamArn = streamArn,
                ShardId = replayShard,
                ShardIteratorType = Amazon.DynamoDBStreams.ShardIteratorType.TRIM_HORIZON,
            })).ShardIterator],
            want: Writes);
        var secondValues = second.Select(t => t.Record.Dynamodb.NewImage["v"].N).ToList();
        Ensure(
            secondValues.SequenceEqual(values, StringComparer.Ordinal),
            "a second TRIM_HORIZON pass over the same shard must replay the identical order; "
            + $"first pass {Py.Repr(values)}, second pass {Py.Repr(secondValues)}");
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
