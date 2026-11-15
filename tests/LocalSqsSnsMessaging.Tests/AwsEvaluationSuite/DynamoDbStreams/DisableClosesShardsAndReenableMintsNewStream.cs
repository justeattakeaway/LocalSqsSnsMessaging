// Translated from behavioral/dynamodbstreams/check_disable_closes_shards_and_reenable_mints_new_stream.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Ddb;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using Streams = Amazon.DynamoDBStreams.Model;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDbStreams;

/// <summary>dynamodbstreams.disable-closes-shards-and-reenable-mints-new-stream</summary>
[Category("AwsEvaluationSuite")]
public sealed class DisableClosesShardsAndReenableMintsNewStream
{
    private const string CheckId = "disable-closes-shards-and-reenable-mints-new-stream";

    /// <summary>The default service-class budget; the harness deadline bounds the run.</summary>
    private const double PollBudgetS = 300.0;

    private static readonly string[] NotFound = ["ResourceNotFoundException"];

    /// <summary>
    /// Disabling a stream closes its shards but keeps it readable; re-enabling mints a new ARN.
    /// The old stream stays DISABLED with its view type and records; ListStreams reports both, and
    /// the deleted table is refused with the exact not-found messages.
    /// Reference: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Streams.html
    /// </summary>
    [Test]
    [DisplayName("dynamodbstreams.disable-closes-shards-and-reenable-mints-new-stream")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var streams = ctx.DynamoStreams;

        var (table, firstArn) = await CreateStreamTable(ctx, ddb, StreamViewType.NEW_AND_OLD_IMAGES, "sim2real-behav-dbslife-");
        var (_, shardIds, horizon) = await WaitStreamUsable(streams, firstArn, timeoutS: PollBudgetS);

        var key = Item(("pk", S("lifecycle-item")));
        await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("lifecycle-item")), ("v", N("1"))) });
        var tagged = await Drain(streams, shardIds, horizon, want: 1);
        Ensure(
            tagged.Count == 1,
            $"the write must reach the stream before it is disabled; drained {tagged.Count} records");
        var (writtenShard, writtenRecord) = tagged[0];
        Ensure(
            writtenRecord.EventName?.Value == "INSERT",
            $"{{'eventID': {Py.Repr(writtenRecord.EventID)}, 'eventName': {Py.Repr(writtenRecord.EventName?.Value)}, 'dynamodb': {{'Keys': {Py.Repr(writtenRecord.Dynamodb?.Keys)}}}}}");

        await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            StreamSpecification = new StreamSpecification { StreamEnabled = false },
        });
        var disabled = await Poll(
            async () => (await streams.DescribeStreamAsync(new Streams.DescribeStreamRequest { StreamArn = firstArn })).StreamDescription,
            d => d.StreamStatus?.Value == "DISABLED",
            ReprDescription,
            description: "the stream reaches DISABLED");
        Ensure(
            disabled.StreamViewType?.Value == "NEW_AND_OLD_IMAGES",
            "a disabled stream keeps the view type it was created with, got "
            + $"{Py.Repr(disabled.StreamViewType?.Value)}");
        var disabledShardIds = (disabled.Shards ?? []).Select(s => s.ShardId).ToList();
        Ensure(
            disabledShardIds.ToHashSet(StringComparer.Ordinal).SetEquals(shardIds),
            $"disabling must not drop shards; had {Py.Repr(shardIds.Order(StringComparer.Ordinal).ToList())}, now "
            + $"{Py.Repr(disabledShardIds.Order(StringComparer.Ordinal).ToList())}");
        foreach (var shard in disabled.Shards ?? [])
        {
            var rng = shard.SequenceNumberRange;
            var ending = rng.EndingSequenceNumber ?? "";
            Ensure(
                ending.Length > 0 && ending.All(char.IsAsciiDigit),
                "every shard of a disabled stream must be CLOSED by an EndingSequenceNumber; shard "
                + $"{shard.ShardId} reported {ReprRange(rng)}");
        }

        var described = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table })).Table;
        Ensure(
            described.StreamSpecification is null,
            "DescribeTable drops StreamSpecification once the stream is off, got "
            + $"{ReprSpec(described.StreamSpecification)}");
        Ensure(
            described.LatestStreamArn == firstArn,
            "LatestStreamArn keeps pointing at the disabled stream, got "
            + $"{Py.Repr(described.LatestStreamArn)}");

        // The record written before the disable is still readable out of the closed shard.
        var replayed = await Drain(
            streams,
            [writtenShard],
            [(await streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
            {
                StreamArn = firstArn,
                ShardId = writtenShard,
                ShardIteratorType = Amazon.DynamoDBStreams.ShardIteratorType.TRIM_HORIZON,
            })).ShardIterator],
            want: 1);
        var replayedKeys = replayed.Select(t => t.Record.Dynamodb.Keys).ToList();
        Ensure(
            replayedKeys.Count == 1 && Py.Equal(replayedKeys[0], key),
            "a disabled stream's closed shard must still replay the records it holds, got "
            + $"{Py.Repr(replayedKeys)}");

        var updated = await ddb.UpdateTableAsync(new UpdateTableRequest
        {
            TableName = table,
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.KEYS_ONLY },
        });
        var secondArn = updated.TableDescription.LatestStreamArn;
        Ensure(
            !string.IsNullOrEmpty(secondArn) && secondArn != firstArn,
            "re-enabling must mint a NEW stream rather than revive the old one; got "
            + $"{Py.Repr(secondArn)} against the original {Py.Repr(firstArn)}");
        var reenabled = await Poll(
            async () => (await streams.DescribeStreamAsync(new Streams.DescribeStreamRequest { StreamArn = secondArn })).StreamDescription,
            d => d.StreamStatus?.Value == "ENABLED",
            ReprDescription,
            description: "the new stream reaches ENABLED");
        Ensure(
            reenabled.StreamViewType?.Value == "KEYS_ONLY",
            "the new stream must carry the view type the re-enable asked for, got "
            + $"{Py.Repr(reenabled.StreamViewType?.Value)}");
        Ensure(
            (await streams.DescribeStreamAsync(new Streams.DescribeStreamRequest { StreamArn = firstArn })).StreamDescription.StreamStatus?.Value == "DISABLED",
            "the original stream must stay DISABLED after a new one is enabled");

        var both = (await ListStreams(streams, table)).Select(s => s.StreamArn).ToHashSet(StringComparer.Ordinal);
        Ensure(
            both.SetEquals([firstArn, secondArn]),
            $"ListStreams must report the disabled stream alongside the new one; got {Py.Repr(both.Order(StringComparer.Ordinal).ToList())}");

        await ddb.DeleteTableAsync(new DeleteTableRequest { TableName = table });
        await Poll(
            () => TableStatus(ddb, table),
            st => st == "GONE",
            st => Py.Repr(st),
            description: "the parent table is deleted");
        var refused = await ExpectError(
            () => ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table }),
            NotFound,
            contains: [table]);
        var expectedMessage = $"Requested resource not found: Table: {table} not found";
        Ensure(refused.Message == expectedMessage, $"expected the message {Py.Repr(expectedMessage)}, got {Py.Repr(refused.Message)}");
        var names = new List<string>();
        string? startTable = null;
        do
        {
            var page = await ddb.ListTablesAsync(new ListTablesRequest { ExclusiveStartTableName = startTable });
            names.AddRange(page.TableNames ?? []);
            startTable = page.LastEvaluatedTableName;
        }
        while (startTable is not null);
        Ensure(!names.Contains(table, StringComparer.Ordinal), $"deleted table {table} still listed by ListTables");
        // AWS's item-level not-found message does not name the table.
        refused = await ExpectError(
            () => ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = Item(("pk", S("lifecycle-item")), ("v", N("2"))) }),
            NotFound);
        expectedMessage = "Requested resource not found";
        Ensure(refused.Message == expectedMessage, $"expected the message {Py.Repr(expectedMessage)}, got {Py.Repr(refused.Message)}");
    }

    /// <summary>Grade a ClientError on code, HTTP status, the exact message and the ids it must name.</summary>
    private static void AssertError(AwsError exc, string[] codes, int status = 400, string[]? contains = null)
    {
        var code = exc.Code;
        var msg = exc.Message ?? "";
        var gotStatus = (int)exc.StatusCode;
        Ensure(codes.Contains(code, StringComparer.Ordinal), $"expected error code in {Py.Repr(codes)}, got {Py.Repr(code)}: {Py.Repr(msg)}");
        Ensure(gotStatus == status, $"expected HTTP {status} with {code}, got {gotStatus}");
        Ensure(msg.Trim().Length > 0, $"expected the exact message with {code}");
        foreach (var want in contains ?? [])
        {
            Ensure(msg.Contains(want, StringComparison.Ordinal), $"expected the {code} message to name {Py.Repr(want)}, got {Py.Repr(msg)}");
        }
    }

    /// <summary>Call fn() and grade the ClientError it raises; an accepted call is itself a failure.</summary>
    private static async Task<AwsError> ExpectError<T>(Func<Task<T>> fn, string[] codes, int status = 400, string[]? contains = null)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected {Py.Repr(codes)}, but the call succeeded: {result}");
        }

        AssertError(error, codes, status, contains);
        return error;
    }

    private static async Task<string> TableStatus(IAmazonDynamoDB ddb, string name)
    {
        try
        {
            return (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = name })).Table.TableStatus.Value;
        }
        catch (AmazonServiceException exc) when (exc.ErrorCode == "ResourceNotFoundException")
        {
            return "GONE";
        }
    }

    private static async Task DeleteTableIfPresent(IAmazonDynamoDB ddb, string name)
    {
        try
        {
            await ddb.DeleteTableAsync(new DeleteTableRequest { TableName = name });
        }
        catch (AmazonServiceException exc) when (exc.ErrorCode == "ResourceNotFoundException")
        {
        }
    }

    private static async Task<T> Poll<T>(
        Func<Task<T>> fn,
        Func<T, bool> pred,
        Func<T, string> repr,
        double timeoutS = PollBudgetS,
        double intervalS = 3.0,
        string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        var value = await fn();
        while (!pred(value))
        {
            if (clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw Fail($"{description} not met within {timeoutS}s; last {repr(value)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            value = await fn();
        }

        return value;
    }

    private static string ReprSpec(StreamSpecification? spec) =>
        spec is null
            ? "None"
            : Py.Repr(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["StreamEnabled"] = spec.StreamEnabled,
                ["StreamViewType"] = spec.StreamViewType?.Value,
            });

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

    private static string ReprDescription(Streams.StreamDescription d) =>
        $"{{'StreamArn': {Py.Repr(d.StreamArn)}, 'StreamStatus': {Py.Repr(d.StreamStatus?.Value)}, 'Shards': ["
        + string.Join(", ", (d.Shards ?? []).Select(s => $"{{'ShardId': {Py.Repr(s.ShardId)}, 'SequenceNumberRange': {ReprRange(s.SequenceNumberRange)}}}"))
        + "]}";

    /// <summary>Every stream for one table, following LastEvaluatedStreamArn to the end.</summary>
    private static async Task<List<Streams.StreamSummary>> ListStreams(Amazon.DynamoDBStreams.IAmazonDynamoDBStreams streams, string tableName)
    {
        var output = new List<Streams.StreamSummary>();
        string? start = null;
        while (true)
        {
            var request = new Streams.ListStreamsRequest { TableName = tableName };
            if (start is not null)
            {
                request.ExclusiveStartStreamArn = start;
            }

            var resp = await streams.ListStreamsAsync(request);
            output.AddRange(resp.Streams ?? []);
            start = resp.LastEvaluatedStreamArn;
            if (start is null)
            {
                return output;
            }
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
        ctx.OnCleanup(() => DeleteTableIfPresent(ddb, name));
        var streamArn = created.TableDescription.LatestStreamArn;
        Ensure(
            !string.IsNullOrEmpty(streamArn),
            "CreateTable with StreamEnabled must mint a LatestStreamArn, got "
            + $"{Py.Repr(created.TableDescription.LatestStreamArn)}");
        return (name, streamArn);
    }

    /// <summary>
    /// Poll until DescribeStream reports shards AND every shard hands back an iterator.
    /// GetShardIterator can answer ResourceNotFoundException for a few seconds after DescribeStream
    /// already reports ENABLED with shards.
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
