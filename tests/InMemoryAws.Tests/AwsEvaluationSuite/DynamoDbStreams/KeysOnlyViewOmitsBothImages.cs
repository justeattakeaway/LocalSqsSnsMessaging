// Translated from behavioral/dynamodbstreams/check_keys_only_view_omits_both_images.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using Streams = Amazon.DynamoDBStreams.Model;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDbStreams;

/// <summary>dynamodbstreams.keys-only-view-omits-both-images</summary>
[Category("AwsEvaluationSuite")]
public sealed class KeysOnlyViewOmitsBothImages
{
    private const string CheckId = "keys-only-view-omits-both-images";
    private static readonly StreamViewType View = StreamViewType.KEYS_ONLY;
    private static readonly string[] ExpectedNames = ["INSERT", "MODIFY", "REMOVE"];
    private static readonly string[] AlwaysPresent = ["ApproximateCreationDateTime", "SequenceNumber", "SizeBytes"];

    /// <summary>
    /// A KEYS_ONLY stream's INSERT, MODIFY and REMOVE records carry the exact Keys, no NewImage and
    /// no OldImage, and report KEYS_ONLY, for an item the table shows holds non-key attributes.
    /// </summary>
    [Test]
    [DisplayName("dynamodbstreams.keys-only-view-omits-both-images")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var streams = ctx.DynamoStreams;

        var (table, streamArn) = await CreateStreamTable(ctx, ddb, View, "sim2real-behav-dbskeys-");
        var (_, shardIds, horizon) = await WaitStreamUsable(streams, streamArn);

        var key = Item(("pk", S("keys-only-item")));
        var body = Item(("pk", S("keys-only-item")), ("v", N("1")), ("note", S("projected-away")));
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = body });

        // Control: the item genuinely holds non-key attributes, so their absence from the stream
        // records below is a projection decision and not an empty item.
        var stored = (await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true })).Item;
        Ensure(Py.Equal(stored, body), $"the table must hold the full item; GetItem returned {Py.Repr(stored)}");

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET v = :v",
            ExpressionAttributeValues = Item((":v", N("2"))),
        });
        await ddb.DeleteItemAsync(new DeleteItemRequest { TableName = table, Key = key });

        var tagged = await Drain(streams, shardIds, horizon, want: 3);
        var records = tagged.Select(t => t.Record).ToList();
        var names = records.Select(rec => rec.EventName?.Value).ToList();
        Ensure(
            names.SequenceEqual(ExpectedNames),
            "one record per mutation, in commit order, with the mutation-specific eventName; got "
            + $"{Py.Repr(names)}");

        foreach (var rec in records)
        {
            var payload = rec.Dynamodb;
            Ensure(
                payload.StreamViewType?.Value == View.Value,
                $"the {rec.EventName?.Value} record must report StreamViewType {View.Value}, got "
                + $"{Py.Repr(payload.StreamViewType?.Value)}");
            Ensure(
                Py.Equal(payload.Keys, key),
                $"the {rec.EventName?.Value} record must carry exactly the item Keys, got "
                + $"{Py.Repr(payload.Keys)}");
            Ensure(
                payload.NewImage is null,
                $"a {View.Value} stream must never project a NewImage; the {rec.EventName?.Value} record "
                + $"carried {Py.Repr(payload.NewImage)}");
            Ensure(
                payload.OldImage is null,
                $"a {View.Value} stream must never project an OldImage; the {rec.EventName?.Value} record "
                + $"carried {Py.Repr(payload.OldImage)}");
            foreach (var field in AlwaysPresent)
            {
                Ensure(
                    PayloadKeys(payload).Contains(field, StringComparer.Ordinal),
                    $"every stream record carries {field} regardless of view type; the "
                    + $"{rec.EventName?.Value} record held {Py.Repr(PayloadKeys(payload))}");
            }

            Ensure(
                rec.EventSource == "aws:dynamodb",
                $"eventSource must be aws:dynamodb, got {Py.Repr(rec.EventSource)}");
            Ensure(
                rec.AwsRegion == ctx.Region,
                $"awsRegion must name the region the write happened in ({ctx.Region}), got "
                + $"{Py.Repr(rec.AwsRegion)}");
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
        var described = (await ddb.DescribeTableAsync(name)).Table;
        Ensure(
            described.StreamSpecification is { StreamEnabled: true } spec && spec.StreamViewType?.Value == viewType.Value,
            "DescribeTable must echo the stream specification, got "
            + $"{ReprSpec(described.StreamSpecification)}");
        return (name, streamArn);
    }

    private static string ReprSpec(StreamSpecification? spec) =>
        spec is null
            ? "None"
            : Py.Repr(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["StreamEnabled"] = spec.StreamEnabled,
                ["StreamViewType"] = spec.StreamViewType?.Value,
            });

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

    /// <summary>The members boto3's <c>rec['dynamodb']</c> dict holds: <c>sorted(payload)</c>.</summary>
    private static List<string> PayloadKeys(Streams.StreamRecord payload)
    {
        (string Name, bool Present)[] members =
        [
            ("ApproximateCreationDateTime", payload.ApproximateCreationDateTime is not null),
            ("Keys", payload.Keys is not null),
            ("NewImage", payload.NewImage is not null),
            ("OldImage", payload.OldImage is not null),
            ("SequenceNumber", payload.SequenceNumber is not null),
            ("SizeBytes", payload.SizeBytes is not null),
            ("StreamViewType", payload.StreamViewType is not null),
        ];
        return members.Where(m => m.Present).Select(m => m.Name).Order(StringComparer.Ordinal).ToList();
    }
}
