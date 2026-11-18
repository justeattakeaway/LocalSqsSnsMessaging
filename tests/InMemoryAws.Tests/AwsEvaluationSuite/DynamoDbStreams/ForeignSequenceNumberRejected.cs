// Translated from behavioral/dynamodbstreams/check_foreign_sequence_number_rejected.py
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

/// <summary>dynamodbstreams.foreign-sequence-number-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ForeignSequenceNumberRejected
{
    private const string CheckId = "foreign-sequence-number-rejected";
    private static readonly string ZeroSequence = new('0', 21);
    private static readonly string[] ExpectedValues = ["7"];

    /// <summary>
    /// GetShardIterator rejects AT_SEQUENCE_NUMBER with a sequence number foreign to the shard or
    /// none at all (ValidationException), while the shard's own sequence number reads its record.
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_streams_GetShardIterator.html
    /// </summary>
    [Test]
    [DisplayName("dynamodbstreams.foreign-sequence-number-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var streams = ctx.DynamoStreams;

        var (table, streamArn) = await CreateStreamTable(ctx, ddb, StreamViewType.NEW_IMAGE, "sim2real-behav-dbsseq-");
        var (_, shardIds, _) = await WaitStreamUsable(streams, streamArn);

        var key = Item(("pk", S("sequence-item")));
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("sequence-item")), ("v", N("7"))) });
        var (holder, ownSequence) = await ShardAndSequenceFor(streams, streamArn, shardIds, key);

        // The Smithy model does not list ValidationException for GetShardIterator; AWS returns it.
        var (code, message) = await Error(() => streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
        {
            StreamArn = streamArn,
            ShardId = holder,
            ShardIteratorType = Amazon.DynamoDBStreams.ShardIteratorType.AT_SEQUENCE_NUMBER,
            SequenceNumber = ZeroSequence,
        }));
        Ensure(
            code == "ValidationException",
            "AT_SEQUENCE_NUMBER on a sequence number foreign to the shard must be "
            + $"ValidationException, got {Py.Repr(code)}");
        Ensure(
            message == "Invalid SequenceNumber for the shard",
            $"AT_SEQUENCE_NUMBER on a foreign sequence number answered message {Py.Repr(message)}");

        (code, message) = await Error(() => streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
        {
            StreamArn = streamArn,
            ShardId = holder,
            ShardIteratorType = Amazon.DynamoDBStreams.ShardIteratorType.AT_SEQUENCE_NUMBER,
        }));
        Ensure(
            code == "ValidationException",
            $"AT_SEQUENCE_NUMBER with no SequenceNumber must be ValidationException, got {Py.Repr(code)}");
        Ensure(
            message == "One or more parameter values were invalid: SequenceNumber cannot be null",
            $"AT_SEQUENCE_NUMBER with no SequenceNumber answered message {Py.Repr(message)}");

        // Effect absent: no iterator was issued, so nothing was positioned -- and the refusals did not
        // damage the shard, which still describes itself as open with its original start.
        var shard = (await streams.DescribeStreamAsync(new Streams.DescribeStreamRequest { StreamArn = streamArn }))
            .StreamDescription.Shards.First(s => s.ShardId == holder);
        Ensure(
            shard.SequenceNumberRange.EndingSequenceNumber is null,
            "the refused iterator requests must not close the shard; range is "
            + $"{ReprRange(shard.SequenceNumberRange)}");
        Ensure(
            BigInteger.Parse(ownSequence, CultureInfo.InvariantCulture)
                >= BigInteger.Parse(shard.SequenceNumberRange.StartingSequenceNumber, CultureInfo.InvariantCulture),
            $"the record's own sequence number {ownSequence} must sit at or after its shard's "
            + $"start {shard.SequenceNumberRange.StartingSequenceNumber}");

        // Positive control: the shard's OWN sequence number is accepted and selects that record.
        var cursor = (await streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
        {
            StreamArn = streamArn,
            ShardId = holder,
            ShardIteratorType = Amazon.DynamoDBStreams.ShardIteratorType.AT_SEQUENCE_NUMBER,
            SequenceNumber = ownSequence,
        })).ShardIterator;
        var got = await Drain(streams, [holder], [cursor], want: 1);
        Ensure(
            got.Select(t => t.Record.Dynamodb.NewImage?["v"].N).SequenceEqual(ExpectedValues),
            "AT_SEQUENCE_NUMBER on the shard's own sequence number must return that record, got "
            + $"{Py.Repr(got.Select(t => t.Record.Dynamodb.NewImage).ToList())}");
        Ensure(
            got[0].Record.Dynamodb.SequenceNumber == ownSequence,
            "the record read back must be the one the sequence number named, got "
            + $"{Py.Repr(got[0].Record.Dynamodb.SequenceNumber)} vs {Py.Repr(ownSequence)}");
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

    /// <summary>
    /// (shard_id, SequenceNumber) of the record for <paramref name="key"/>. Which shard a partition
    /// key lands on is not knowable in advance, so poll every shard.
    /// </summary>
    private static async Task<(string ShardId, string SequenceNumber)> ShardAndSequenceFor(
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
                        return (sid, record.Dynamodb.SequenceNumber);
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
