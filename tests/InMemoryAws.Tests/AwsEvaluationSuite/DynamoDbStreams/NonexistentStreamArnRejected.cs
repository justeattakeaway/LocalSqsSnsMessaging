// Translated from behavioral/dynamodbstreams/check_nonexistent_stream_arn_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using Streams = Amazon.DynamoDBStreams.Model;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDbStreams;

/// <summary>dynamodbstreams.nonexistent-stream-arn-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class NonexistentStreamArnRejected
{
    private const string CheckId = "nonexistent-stream-arn-rejected";

    /// <summary>
    /// DescribeStream and GetShardIterator reject a well-formed stream ARN naming a missing table
    /// with ResourceNotFoundException (each with its own message), leave no stream or table behind,
    /// and the same two calls succeed on a real stream.
    /// </summary>
    [Test]
    [DisplayName("dynamodbstreams.nonexistent-stream-arn-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var streams = ctx.DynamoStreams;

        var (table, streamArn) = await CreateStreamTable(ctx, ddb, StreamViewType.KEYS_ONLY, "sim2real-behav-dbsnf-");
        var (_, shardIds, _) = await WaitStreamUsable(streams, streamArn);

        // The bogus ARN differs from the real one in the TABLE component and nothing else.
        var absentTable = table + "-absent";
        var label = streamArn[(streamArn.LastIndexOf('/') + 1)..];
        var bogusArn =
            $"arn:aws:dynamodb:{ctx.Region}:{streamArn.Split(':')[4]}:table/{absentTable}"
            + $"/stream/{label}";

        var (code, message) = await Error(() => streams.DescribeStreamAsync(new Streams.DescribeStreamRequest { StreamArn = bogusArn }));
        Ensure(
            code == "ResourceNotFoundException",
            "DescribeStream on a stream ARN whose table does not exist must be "
            + $"ResourceNotFoundException, got {Py.Repr(code)}");
        Ensure(
            message == $"Requested resource not found: Stream: {bogusArn} not found",
            $"DescribeStream on a stream ARN whose table does not exist answered message {Py.Repr(message)}");

        (code, message) = await Error(() => streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
        {
            StreamArn = bogusArn,
            ShardId = shardIds[0],
            ShardIteratorType = Amazon.DynamoDBStreams.ShardIteratorType.TRIM_HORIZON,
        }));
        Ensure(
            code == "ResourceNotFoundException",
            "GetShardIterator on a stream ARN whose table does not exist must be "
            + $"ResourceNotFoundException, got {Py.Repr(code)}");
        Ensure(
            message == "Requested resource not found: Stream does not exist",
            "GetShardIterator on a stream ARN whose table does not exist answered message "
            + $"{Py.Repr(message)}");

        // Effect absent: the rejected calls did not conjure a stream or a table.
        Ensure(
            (await ListStreams(streams, absentTable)).Count == 0,
            $"the rejected calls must leave no stream behind for {absentTable}");
        (code, message) = await Error(() => ddb.DescribeTableAsync(new DescribeTableRequest { TableName = absentTable }));
        Ensure(
            code == "ResourceNotFoundException",
            $"the table named by the bogus ARN must still not exist, got {Py.Repr(code)}");
        Ensure(
            message == $"Requested resource not found: Table: {absentTable} not found",
            $"DescribeTable on the table named by the bogus ARN answered message {Py.Repr(message)}");

        // Positive control: the same two operations against the real stream.
        var live = (await streams.DescribeStreamAsync(new Streams.DescribeStreamRequest { StreamArn = streamArn })).StreamDescription;
        Ensure(
            live.StreamArn == streamArn,
            $"the control DescribeStream must answer for the real stream, got {Py.Repr(live.StreamArn)}");
        var iterator = (await streams.GetShardIteratorAsync(new Streams.GetShardIteratorRequest
        {
            StreamArn = streamArn,
            ShardId = shardIds[0],
            ShardIteratorType = Amazon.DynamoDBStreams.ShardIteratorType.TRIM_HORIZON,
        })).ShardIterator;
        Ensure(!string.IsNullOrEmpty(iterator), "the control GetShardIterator must return a usable iterator");
        Ensure(
            (await streams.GetRecordsAsync(new Streams.GetRecordsRequest { ShardIterator = iterator })).Records is not null,
            "the control iterator must be accepted by GetRecords");
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
}
