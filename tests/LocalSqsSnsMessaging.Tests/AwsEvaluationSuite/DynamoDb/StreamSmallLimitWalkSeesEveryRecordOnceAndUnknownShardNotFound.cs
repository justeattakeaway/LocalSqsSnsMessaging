// Translated from behavioral/dynamodb/check_stream_small_limit_walk_sees_every_record_once_and_unknown_shard_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using IAmazonDynamoDBStreams = Amazon.DynamoDBStreams.IAmazonDynamoDBStreams;
using ShardIteratorType = Amazon.DynamoDBStreams.ShardIteratorType;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using Streams = Amazon.DynamoDBStreams.Model;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.stream-small-limit-walk-sees-every-record-once-and-unknown-shard-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class StreamSmallLimitWalkSeesEveryRecordOnceAndUnknownShardNotFound
{
    private static readonly string[] Keys = [.. Enumerable.Range(0, 8).Select(i => "k" + i.ToString(CultureInfo.InvariantCulture))];
    private const int Limit = 2;
    private const string UnknownShard = "shardId-00000000000000000000-00000000";
    private const string UnknownShardMessage = "Requested resource not found: Shard does not exist";

    /// <summary>A cleanup running undo() where a failed delete raises; in-use is retried every 2 s.</summary>
    private static Func<Task> Teardown(Func<Task> undo, string[] notFound, double timeoutS = 300.0) => async () =>
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(undo);
            if (error is null || notFound.Contains(error.Code))
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    };

    /// <summary>Poll DescribeTable every 2 s until the table and its indexes are ACTIVE.</summary>
    private static async Task<TableDescription> WaitActive(IAmazonDynamoDB ddb, FidelityContext ctx, string table)
    {
        var clock = FidelityTime.StartNew();
        var budget = TimeSpan.FromSeconds(Math.Max(0.0, ctx.TimeLeftSeconds - 30.0));
        TableDescription? desc = null;
        while (clock.Elapsed < budget)
        {
            desc = (await ddb.DescribeTableAsync(table)).Table;
            if (desc.TableStatus?.Value == "ACTIVE" && (desc.GlobalSecondaryIndexes ?? []).All(g => g.IndexStatus?.Value == "ACTIVE"))
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"table {table} not ACTIVE before the deadline: {desc}");
    }

    /// <summary>(Error.Code, Error.Message) of the error fn() raises; an unexpected success fails.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected the call to be refused, but it succeeded");
        }

        return (error.Code, error.Message);
    }

    private static async Task<List<string>> Shards(IAmazonDynamoDBStreams streams, string arn)
    {
        var shards = new List<string>();
        string? start = null;
        while (true)
        {
            var request = new Streams.DescribeStreamRequest { StreamArn = arn };
            if (!string.IsNullOrEmpty(start))
            {
                request.ExclusiveStartShardId = start;
            }

            var desc = (await streams.DescribeStreamAsync(request)).StreamDescription;
            shards.AddRange((desc.Shards ?? []).Select(s => s.ShardId));
            start = desc.LastEvaluatedShardId;
            if (string.IsNullOrEmpty(start))
            {
                return shards;
            }
        }
    }

    /// <summary>
    /// Walking every stream shard with GetRecords Limit=2 returns pages of at most 2 that together
    /// carry each of 8 inserts exactly once; GetShardIterator for an unknown shard is not found.
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_streams_GetRecords.html
    /// </summary>
    [Test]
    [DisplayName("dynamodb.stream-small-limit-walk-sees-every-record-once-and-unknown-shard-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var streams = ctx.DynamoStreams;
        var table = FidelityContext.UniqueName("sim2real-behav-ddb-strwalk-");
        var created = await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.KEYS_ONLY },
        });
        ctx.OnCleanup(Teardown(() => ddb.DeleteTableAsync(table), ["ResourceNotFoundException"]));
        await WaitActive(ddb, ctx, table);
        var arn = created.TableDescription.LatestStreamArn;
        foreach (var key in Keys)
        {
            await ddb.PutItemAsync(table, Item(("pk", S(key))));
        }

        var clock = FidelityTime.StartNew();
        var budget = TimeSpan.FromSeconds(Math.Max(0.0, ctx.TimeLeftSeconds - 30.0));
        var shardOrder = new List<string>();
        var cursors = new Dictionary<string, string?>(StringComparer.Ordinal);
        var records = new List<Streams.Record>();
        var oversized = new List<int>();
        while (clock.Elapsed < budget)
        {
            foreach (var shard in await Shards(streams, arn))
            {
                if (!cursors.ContainsKey(shard))
                {
                    shardOrder.Add(shard);
                    cursors[shard] = (await streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
                    {
                        StreamArn = arn,
                        ShardId = shard,
                        ShardIteratorType = ShardIteratorType.TRIM_HORIZON,
                    })).ShardIterator;
                }
            }

            foreach (var shard in shardOrder)
            {
                var cursor = cursors[shard];
                if (cursor is null)
                {
                    continue;
                }

                var page = await streams.GetRecordsAsync(new Streams.GetRecordsRequest { ShardIterator = cursor, Limit = Limit });
                var got = page.Records ?? [];
                if (got.Count > Limit)
                {
                    oversized.Add(got.Count);
                }

                records.AddRange(got);
                cursors[shard] = page.NextShardIterator;
            }

            if (records.Count >= Keys.Length)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        // Drain each shard to an empty page so a repeated record after the eighth is seen too.
        foreach (var shard in shardOrder)
        {
            var cursor = cursors[shard];
            while (cursor is not null && clock.Elapsed < budget)
            {
                var page = await streams.GetRecordsAsync(new Streams.GetRecordsRequest { ShardIterator = cursor, Limit = Limit });
                var got = page.Records ?? [];
                if (got.Count > Limit)
                {
                    oversized.Add(got.Count);
                }

                records.AddRange(got);
                cursor = page.NextShardIterator;
                if (got.Count == 0)
                {
                    break;
                }
            }
        }

        Ensure(cursors.Count > 0, $"the stream {arn} never listed a shard");
        Ensure(oversized.Count == 0, $"GetRecords with Limit={Limit} returned pages of {Py.Repr(oversized)} records");
        var eventIds = records.Select(r => r.EventID).ToList();
        Ensure(eventIds.Count == eventIds.Distinct(StringComparer.Ordinal).Count(), $"the walk saw a record twice: {Py.Repr(eventIds)}");
        var keys = records.Select(r => r.Dynamodb.Keys["pk"].S).Order(StringComparer.Ordinal).ToList();
        Ensure(keys.SequenceEqual(Keys), $"the walk saw keys {Py.Repr(keys)}, expected each of {Py.Repr(Keys)} once");
        var names = records.Select(r => r.EventName?.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Ensure(names.SequenceEqual(["INSERT"]), $"the walk saw event names {Py.Repr(names)}");

        var (code, msg) = await Error(() => streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
        {
            StreamArn = arn,
            ShardId = UnknownShard,
            ShardIteratorType = ShardIteratorType.TRIM_HORIZON,
        }));
        Ensure(
            (code, msg) == ("ResourceNotFoundException", UnknownShardMessage),
            $"GetShardIterator for the unknown shard {Py.Repr(UnknownShard)} gave {Py.Repr(code)}: {Py.Repr(msg)}");
    }
}
