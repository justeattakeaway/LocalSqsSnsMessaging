// Translated from behavioral/dynamodbstreams/check_malformed_shard_iterator_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using Streams = Amazon.DynamoDBStreams.Model;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDbStreams;

/// <summary>dynamodbstreams.malformed-shard-iterator-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class MalformedShardIteratorRejected
{
    private const string CheckId = "malformed-shard-iterator-rejected";
    private static readonly string[] ExpectedValues = ["1"];

    /// <summary>
    /// GetRecords rejects an arbitrary string and a truncated real iterator with ValidationException
    /// 'Invalid ShardIterator', and the rejections leave the intact iterator able to replay the record.
    /// </summary>
    [Test]
    [DisplayName("dynamodbstreams.malformed-shard-iterator-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var streams = ctx.DynamoStreams;

        var (table, streamArn) = await CreateStreamTable(ctx, ddb, StreamViewType.NEW_IMAGE, "sim2real-behav-dbsiterr-");
        var (_, shardIds, _) = await WaitStreamUsable(streams, streamArn);

        var key = Item(("pk", S("iterator-item")));
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("iterator-item")), ("v", N("1"))) });

        // The shard that actually carries the record, and an iterator onto it that stays untouched
        // while the malformed ones are rejected.
        var (holder, intact) = await ShardHoldingRecord(streams, streamArn, shardIds, key);

        var (code, message) = await Error(() => streams.GetRecordsAsync(new Streams.GetRecordsRequest
        {
            ShardIterator = "sim2real-behav-not-an-iterator",
        }));
        // AWS answers ValidationException here although the Smithy model does not list it for GetRecords.
        Ensure(
            code == "ValidationException",
            "GetRecords with an arbitrary string as the iterator must be ValidationException, got "
            + $"{Py.Repr(code)}");
        Ensure(
            message == "Invalid ShardIterator",
            $"GetRecords with an arbitrary string as the iterator answered message {Py.Repr(message)}");

        var truncated = intact[..^10];
        (code, message) = await Error(() => streams.GetRecordsAsync(new Streams.GetRecordsRequest { ShardIterator = truncated }));
        Ensure(
            code == "ValidationException",
            "GetRecords with a truncated copy of a real iterator must be ValidationException, got "
            + $"{Py.Repr(code)}");
        Ensure(
            message == "Invalid ShardIterator",
            $"GetRecords with a truncated copy of a real iterator answered message {Py.Repr(message)}");

        // Effect absent: neither rejection consumed or invalidated the caller's real position.
        var replayed = await Drain(streams, [holder], [intact], want: 1);
        var replayedKeys = replayed.Select(t => t.Record.Dynamodb.Keys).ToList();
        Ensure(
            replayedKeys.Count == 1 && Py.Equal(replayedKeys[0], key),
            "the intact iterator must still replay the record after the rejected calls, got "
            + $"{Py.Repr(replayedKeys)}");

        // Positive control: a freshly issued iterator on the same shard is accepted and reads the
        // same record, so the operation itself is healthy.
        var fresh = (await streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
        {
            StreamArn = streamArn,
            ShardId = holder,
            ShardIteratorType = Amazon.DynamoDBStreams.ShardIteratorType.TRIM_HORIZON,
        })).ShardIterator;
        var control = await Drain(streams, [holder], [fresh], want: 1);
        Ensure(
            control.Select(t => t.Record.Dynamodb.NewImage?["v"].N).SequenceEqual(ExpectedValues),
            "the control iterator must read the written record, got "
            + $"{Py.Repr(control.Select(t => t.Record.Dynamodb.NewImage).ToList())}");
    }

    /// <summary>
    /// (shard_id, a fresh TRIM_HORIZON iterator) for the shard that carries <paramref name="key"/>'s record.
    /// Which shard a partition key lands on is not knowable in advance -- a freshly enabled stream
    /// may have several -- so this polls every shard until one delivers the record, then returns a
    /// NEW iterator onto that shard so the caller's position is untouched.
    /// </summary>
    private static async Task<(string ShardId, string Iterator)> ShardHoldingRecord(
        Amazon.DynamoDBStreams.IAmazonDynamoDBStreams streams,
        string streamArn,
        List<string> shardIds,
        Dictionary<string, AttributeValue> key,
        double timeoutS = 150.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            foreach (var sid in shardIds)
            {
                var cursor = (await streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
                {
                    StreamArn = streamArn,
                    ShardId = sid,
                    ShardIteratorType = Amazon.DynamoDBStreams.ShardIteratorType.TRIM_HORIZON,
                })).ShardIterator;
                foreach (var record in (await streams.GetRecordsAsync(new Streams.GetRecordsRequest { ShardIterator = cursor })).Records ?? [])
                {
                    if (Py.Equal(record.Dynamodb.Keys, key))
                    {
                        return (sid, (await streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
                        {
                            StreamArn = streamArn,
                            ShardId = sid,
                            ShardIteratorType = Amazon.DynamoDBStreams.ShardIteratorType.TRIM_HORIZON,
                        })).ShardIterator);
                    }
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        throw Fail($"no shard of {streamArn} delivered a record for {Py.Repr(key)} within {timeoutS}s");
    }

    /// <summary>
    /// The AWS (code, message) a call raised; raises if the call succeeds, so wrongful
    /// acceptance fails loudly instead of reading as a code mismatch.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> call)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(call);
        if (error is null)
        {
            throw Fail($"the call was expected to be rejected but SUCCEEDED, returning {result}");
        }

        return (error.Code, error.Message);
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
