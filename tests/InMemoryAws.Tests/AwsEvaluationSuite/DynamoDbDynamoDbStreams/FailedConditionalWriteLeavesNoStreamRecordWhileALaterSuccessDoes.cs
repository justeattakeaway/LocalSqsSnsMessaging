// Translated from behavioral/dynamodb__dynamodbstreams/check_failed_conditional_write_leaves_no_stream_record_while_a_later_success_does.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using IAmazonDynamoDBStreams = Amazon.DynamoDBStreams.IAmazonDynamoDBStreams;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using Streams = Amazon.DynamoDBStreams.Model;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDbDynamoDbStreams;

/// <summary>dynamodb__dynamodbstreams.failed-conditional-write-leaves-no-stream-record-while-a-later-success-does</summary>
[Category("AwsEvaluationSuite")]
public sealed class FailedConditionalWriteLeavesNoStreamRecordWhileALaterSuccessDoes
{
    private const string CheckId = "failed-conditional-write-leaves-no-stream-record-while-a-later-success-does";
    private static readonly StreamViewType ViewType = StreamViewType.NEW_AND_OLD_IMAGES;
    private const double ShardsBudgetS = 60.0;
    private const double LatencyBudgetS = 60.0;

    /// <summary>The (Code, Message) fn() raises. An unexpected success raises too, naming what came back.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message ?? "");
        }

        throw Fail($"expected a ClientError, but the call succeeded: {Py.Repr(result)}");
    }

    private static async Task<List<string>> ShardIds(IAmazonDynamoDBStreams streams, string streamArn)
    {
        var desc = (await streams.DescribeStreamAsync(new Streams.DescribeStreamRequest { StreamArn = streamArn })).StreamDescription;
        return [.. (desc.Shards ?? []).Select(s => s.ShardId)];
    }

    private static async Task<T> Poll<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, double intervalS = 1.0, string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        var last = await probe();
        while (!predicate(last))
        {
            if (clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw Fail($"{description}: not met within {timeoutS}s; last {Py.Repr(last)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            last = await probe();
        }

        return last;
    }

    /// <summary>
    /// (tagged, advanced_cursors): every record newly read from every open shard, tagged by shard,
    /// until until() first returns True, with each shard's cursor advanced past what was read.
    /// </summary>
    private static async Task<(List<(string ShardId, Streams.Record Record)> Tagged, Dictionary<string, string?> Cursors)> Drain(
        IAmazonDynamoDBStreams streams, Dictionary<string, string?> cursors, Func<List<(string ShardId, Streams.Record Record)>, bool> until, double intervalS = 1.0)
    {
        var tagged = new List<(string ShardId, Streams.Record Record)>();
        cursors = new Dictionary<string, string?>(cursors);
        while (true)
        {
            var progressed = false;
            foreach (var (shardId, cursor) in cursors.ToList())
            {
                if (cursor is null)
                {
                    continue;
                }

                var resp = await streams.GetRecordsAsync(new Streams.GetRecordsRequest { ShardIterator = cursor });
                var got = resp.Records ?? [];
                if (got.Count > 0)
                {
                    tagged.AddRange(got.Select(r => (shardId, r)));
                    progressed = true;
                }

                cursors[shardId] = resp.NextShardIterator;
            }

            if (until(tagged))
            {
                return (tagged, cursors);
            }

            if (!progressed)
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            }
        }
    }

    private static string Repr(List<(string ShardId, Streams.Record Record)> tagged) =>
        Py.Repr(tagged.Select(t => (t.ShardId, t.Record.EventName?.Value, t.Record.Dynamodb?.SequenceNumber)).ToList());

    /// <summary>
    /// A conditional UpdateItem that fails its condition produces no stream record for the
    /// attempted change, while an otherwise identical call that meets the condition does.
    /// </summary>
    [Test]
    [DisplayName("dynamodb__dynamodbstreams.failed-conditional-write-leaves-no-stream-record-while-a-later-success-does")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var streams = ctx.DynamoStreams;

        var table = FidelityContext.UniqueName("sim2real-behav-condstream-");
        var created = await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = ViewType },
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var streamArn = created.TableDescription.LatestStreamArn;
        var shardIds = await Poll(
            () => ShardIds(streams, streamArn),
            ids => ids.Count > 0,
            timeoutS: ShardsBudgetS,
            description: "DescribeStream reporting at least one shard");
        var cursors = new Dictionary<string, string?>();
        foreach (var sid in shardIds)
        {
            cursors[sid] = (await streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
            {
                StreamArn = streamArn,
                ShardId = sid,
                ShardIteratorType = Amazon.DynamoDBStreams.ShardIteratorType.TRIM_HORIZON,
            })).ShardIterator;
        }

        // --- positive control: a plain write lands on the table and, measurably, on the stream ---
        var writeStarted = FidelityTime.StartNew();
        await ddb.PutItemAsync(table, Item(("pk", S("widget")), ("version", N("1"))));
        (var firstBatch, cursors) = await Drain(streams, cursors, until: got => got.Count >= 1 || writeStarted.Elapsed.TotalSeconds > LatencyBudgetS);
        Ensure(firstBatch.Count == 1, $"expected exactly one stream record after the PutItem, got {Repr(firstBatch)}");
        var latencyS = writeStarted.Elapsed.TotalSeconds;
        var (_, firstRecord) = firstBatch[0];
        Ensure(firstRecord.EventName?.Value == "INSERT", $"the PutItem record's eventName was {Py.Repr(firstRecord.EventName?.Value)}, expected 'INSERT'");

        // --- break exactly one thing: a condition the current item does not satisfy ---
        var (code, message) = await Error(() => ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("widget"))),
            UpdateExpression = "SET version = :bad_new",
            ConditionExpression = "version = :bad_cur",
            ExpressionAttributeValues = Item((":bad_new", N("97")), (":bad_cur", N("99"))),
        }));
        Ensure(code == "ConditionalCheckFailedException", $"expected ConditionalCheckFailedException, got {code}");
        Ensure(message == "The conditional request failed", $"unexpected message {Py.Repr(message)}");

        // --- the refused write is absent from the stream over a window derived from the measured
        // positive latency, not a single read or a fixed sleep ---
        var windowS = Math.Max(10.0, 3.0 * latencyS);
        var window = FidelityTime.StartNew();
        (var absentCheck, cursors) = await Drain(streams, cursors, until: _ => window.Elapsed.TotalSeconds >= windowS);
        Ensure(
            absentCheck.Count == 0,
            $"the failed conditional UpdateItem produced {absentCheck.Count} stream record(s), expected none: {Repr(absentCheck)}");

        // --- restore: the identical call, now satisfying its condition, succeeds and is recorded ---
        var restoreStarted = FidelityTime.StartNew();
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = table,
            Key = Item(("pk", S("widget"))),
            UpdateExpression = "SET version = :new",
            ConditionExpression = "version = :cur",
            ExpressionAttributeValues = Item((":new", N("2")), (":cur", N("1"))),
        });
        (var secondBatch, cursors) = await Drain(streams, cursors, until: got => got.Count >= 1 || restoreStarted.Elapsed.TotalSeconds > LatencyBudgetS);
        Ensure(secondBatch.Count == 1, $"expected exactly one stream record after the successful UpdateItem, got {Repr(secondBatch)}");
        var (_, secondRecord) = secondBatch[0];
        Ensure(
            secondRecord.EventName?.Value == "MODIFY",
            $"the successful UpdateItem record's eventName was {Py.Repr(secondRecord.EventName?.Value)}, expected 'MODIFY'");
        var newVersion = secondRecord.Dynamodb.NewImage?.GetValueOrDefault("version");
        Ensure(
            newVersion is not null && Py.Equal(newVersion, N("2")),
            $"the MODIFY record's NewImage.version was {Py.Repr(newVersion)}, expected {{'N': '2'}}");
        var oldVersion = secondRecord.Dynamodb.OldImage?.GetValueOrDefault("version");
        Ensure(
            oldVersion is not null && Py.Equal(oldVersion, N("1")),
            $"the MODIFY record's OldImage.version was {Py.Repr(oldVersion)}, expected {{'N': '1'}}, never the condition's rejected candidate");
    }
}
