// Translated from behavioral/dynamodbstreams/check_iterator_types_select_expected_records.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using Streams = Amazon.DynamoDBStreams.Model;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDbStreams;

/// <summary>dynamodbstreams.iterator-types-select-expected-records</summary>
[Category("AwsEvaluationSuite")]
public sealed class IteratorTypesSelectExpectedRecords
{
    private const int Writes = 5;
    private static readonly string[] HorizonExpected = ["1", "2", "3", "4", "5"];
    private static readonly string[] AtThirdExpected = ["3", "4", "5"];
    private static readonly string[] AfterThirdExpected = ["4", "5"];
    private static readonly string[] TailExpected = ["99"];

    /// <summary>
    /// Each ShardIteratorType selects an exact slice of five records: TRIM_HORIZON all, AT_SEQUENCE_NUMBER
    /// inclusive, AFTER_SEQUENCE_NUMBER exclusive, past-the-end none with a NextShardIterator, and LATEST
    /// none of the history but a later write.
    /// </summary>
    [Test]
    [DisplayName("dynamodbstreams.iterator-types-select-expected-records")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var streams = ctx.DynamoStreams;

        var (table, streamArn) = await CreateStreamTable(ctx, ddb, StreamViewType.NEW_IMAGE, "sim2real-behav-dbsiter-");
        var (_, shardIds, horizon) = await WaitStreamUsable(streams, streamArn);

        for (var n = 1; n <= Writes; n++)
        {
            await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = table,
                Item = Item(("pk", S("iter-item")), ("v", N(n.ToString(CultureInfo.InvariantCulture)))),
            });
        }

        var tagged = await Drain(streams, shardIds, horizon, want: Writes);
        Ensure(tagged.Count == Writes, $"TRIM_HORIZON must replay all {Writes} writes; got {tagged.Count}");
        var landed = tagged.Select(t => t.ShardId).ToHashSet(StringComparer.Ordinal);
        Ensure(
            landed.Count == 1,
            "all writes to one key must land on one shard for these slices to be comparable; got "
            + $"{Py.Repr(landed.Order(StringComparer.Ordinal).ToList())}");
        var shard = landed.Single();
        var seqs = tagged.Select(t => t.Record.Dynamodb.SequenceNumber).ToList();
        var horizonValues = tagged.Select(t => t.Record.Dynamodb.NewImage["v"].N).ToList();
        Ensure(
            horizonValues.SequenceEqual(HorizonExpected),
            $"TRIM_HORIZON must replay from the start of the shard in order; got {Py.Repr(horizonValues)}");

        var atThird = await ReadSlice(streams, streamArn, shard, Amazon.DynamoDBStreams.ShardIteratorType.AT_SEQUENCE_NUMBER, seqs[2], want: 3);
        Ensure(
            atThird.SequenceEqual(AtThirdExpected),
            "AT_SEQUENCE_NUMBER is inclusive of the named record, so it must return 3,4,5; got "
            + $"{Py.Repr(atThird)}");

        var afterThird = await ReadSlice(
            streams, streamArn, shard, Amazon.DynamoDBStreams.ShardIteratorType.AFTER_SEQUENCE_NUMBER, seqs[2], want: 2);
        Ensure(
            afterThird.SequenceEqual(AfterThirdExpected),
            "AFTER_SEQUENCE_NUMBER is exclusive of the named record, so it must return 4,5; got "
            + $"{Py.Repr(afterThird)}");

        var pastEnd = (await streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
        {
            StreamArn = streamArn,
            ShardId = shard,
            ShardIteratorType = Amazon.DynamoDBStreams.ShardIteratorType.AFTER_SEQUENCE_NUMBER,
            SequenceNumber = seqs[^1],
        })).ShardIterator;
        var resp = await streams.GetRecordsAsync(new Streams.GetRecordsRequest { ShardIterator = pastEnd });
        Ensure(
            resp.Records is { Count: 0 },
            "AFTER the last record there is nothing left to read; got "
            + $"{Py.Repr((resp.Records ?? []).Select(r => r.Dynamodb.NewImage["v"].N).ToList())}");
        Ensure(
            !string.IsNullOrEmpty(resp.NextShardIterator),
            "an OPEN shard read past its end must still hand back a NextShardIterator so a consumer "
            + $"keeps its place; response held {Py.Repr(ResponseKeys(resp))}");

        var latest = (await streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
        {
            StreamArn = streamArn,
            ShardId = shard,
            ShardIteratorType = Amazon.DynamoDBStreams.ShardIteratorType.LATEST,
        })).ShardIterator;
        var first = await streams.GetRecordsAsync(new Streams.GetRecordsRequest { ShardIterator = latest });
        Ensure(
            first.Records is { Count: 0 },
            "a LATEST iterator taken after the history must see none of it; got "
            + $"{Py.Repr((first.Records ?? []).Select(r => r.Dynamodb.NewImage["v"].N).ToList())}");
        Ensure(
            !string.IsNullOrEmpty(first.NextShardIterator),
            "GetRecords on an open shard must return a NextShardIterator even with zero records; "
            + $"response held {Py.Repr(ResponseKeys(first))}");

        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("iter-item")), ("v", N("99"))) });
        var tail = await Drain(streams, [shard], [first.NextShardIterator], want: 1);
        var tailValues = tail.Select(t => t.Record.Dynamodb.NewImage["v"].N).ToList();
        Ensure(
            tailValues.SequenceEqual(TailExpected),
            "the LATEST-anchored iterator must deliver the post-anchor write and nothing earlier; "
            + $"got {Py.Repr(tailValues)}");
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

    /// <summary>The `v` values GetRecords yields for one iterator type anchored at one sequence number.</summary>
    private static async Task<List<string>> ReadSlice(
        Amazon.DynamoDBStreams.IAmazonDynamoDBStreams streams,
        string streamArn,
        string shard,
        Amazon.DynamoDBStreams.ShardIteratorType iteratorType,
        string sequenceNumber,
        int want)
    {
        var cursor = (await streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
        {
            StreamArn = streamArn,
            ShardId = shard,
            ShardIteratorType = iteratorType,
            SequenceNumber = sequenceNumber,
        })).ShardIterator;
        return (await Drain(streams, [shard], [cursor], want: want)).Select(t => t.Record.Dynamodb.NewImage["v"].N).ToList();
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
            Tags = [new Tag { Key = "simparity-check", Value = "iterator-types-select-expected-records" }],
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
