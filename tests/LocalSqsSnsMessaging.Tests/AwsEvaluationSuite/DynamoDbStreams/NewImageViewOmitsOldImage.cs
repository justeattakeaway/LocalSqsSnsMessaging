// Translated from behavioral/dynamodbstreams/check_new_image_view_omits_old_image.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using Streams = Amazon.DynamoDBStreams.Model;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDbStreams;

/// <summary>dynamodbstreams.new-image-view-omits-old-image</summary>
[Category("AwsEvaluationSuite")]
public sealed class NewImageViewOmitsOldImage
{
    private const string CheckId = "new-image-view-omits-old-image";
    private static readonly StreamViewType View = StreamViewType.NEW_IMAGE;
    private static readonly string[] ExpectedNames = ["INSERT", "MODIFY", "REMOVE"];

    /// <summary>
    /// A NEW_IMAGE stream's INSERT and MODIFY records carry the item as it is after the write, no
    /// record carries an OldImage, and the REMOVE record carries neither image, only Keys.
    /// </summary>
    [Test]
    [DisplayName("dynamodbstreams.new-image-view-omits-old-image")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var streams = ctx.DynamoStreams;

        var (table, streamArn) = await CreateStreamTable(ctx, ddb, View, "sim2real-behav-dbsnew-");
        var (_, shardIds, horizon) = await WaitStreamUsable(streams, streamArn);

        var key = Item(("pk", S("new-image-item")));
        var inserted = Item(("pk", S("new-image-item")), ("v", N("1")), ("note", S("first")));
        var updated = Item(("pk", S("new-image-item")), ("v", N("2")), ("note", S("first")));
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = inserted });
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = key,
            UpdateExpression = "SET v = :v",
            ExpressionAttributeValues = Item((":v", N("2"))),
        });
        await ddb.DeleteItemAsync(new DeleteItemRequest { TableName = table, Key = key });

        // Independent confirmation that the delete actually took effect at the table -- a read
        // through a DIFFERENT operation than the mutation, and on the data plane the stream records
        // below are never asked to speak for.
        var afterDelete = await ddb.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true });
        Ensure(
            afterDelete.Item is null,
            "after DeleteItem the item must be gone from the table; GetItem returned "
            + $"{Py.Repr(afterDelete.Item)}");

        var tagged = await Drain(streams, shardIds, horizon, want: 3);
        var records = tagged.Select(t => t.Record).ToList();
        var names = records.Select(rec => rec.EventName?.Value).ToList();
        Ensure(
            names.SequenceEqual(ExpectedNames),
            "one record per mutation, in commit order, with the mutation-specific eventName; got "
            + $"{Py.Repr(names)}");
        var (insert, modify, remove) = (records[0], records[1], records[2]);

        Ensure(
            Py.Equal(insert.Dynamodb.NewImage, inserted),
            "the INSERT record's NewImage must be the item as written, got "
            + $"{Py.Repr(insert.Dynamodb.NewImage)}");
        Ensure(
            Py.Equal(modify.Dynamodb.NewImage, updated),
            "the MODIFY record's NewImage must be the item AFTER the update, got "
            + $"{Py.Repr(modify.Dynamodb.NewImage)}");
        Ensure(
            remove.Dynamodb.NewImage is null,
            $"a delete produces no resulting item, so a {View.Value} REMOVE record must carry no "
            + $"NewImage; got {Py.Repr(remove.Dynamodb.NewImage)}");

        foreach (var rec in records)
        {
            var payload = rec.Dynamodb;
            Ensure(
                payload.OldImage is null,
                $"a {View.Value} stream must never project an OldImage; the {rec.EventName?.Value} record "
                + $"carried {Py.Repr(payload.OldImage)}");
            Ensure(
                payload.StreamViewType?.Value == View.Value,
                $"the {rec.EventName?.Value} record must report StreamViewType {View.Value}, got "
                + $"{Py.Repr(payload.StreamViewType?.Value)}");
            Ensure(
                Py.Equal(payload.Keys, key),
                "every record carries the item Keys regardless of view type; the "
                + $"{rec.EventName?.Value} record carried {Py.Repr(payload.Keys)}");
            Ensure(
                rec.EventVersion == "1.1",
                $"the record format version must be 1.1, got {Py.Repr(rec.EventVersion)}");
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
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
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
}
