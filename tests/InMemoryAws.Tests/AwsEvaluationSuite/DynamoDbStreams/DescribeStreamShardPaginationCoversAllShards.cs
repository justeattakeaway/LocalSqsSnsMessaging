// Translated from behavioral/dynamodbstreams/check_describe_stream_shard_pagination_covers_all_shards.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using static InMemoryAws.Tests.AwsEvaluationSuite.Ddb;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using Streams = Amazon.DynamoDBStreams.Model;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDbStreams;

/// <summary>dynamodbstreams.describe-stream-shard-pagination-covers-all-shards</summary>
[Category("AwsEvaluationSuite")]
public sealed class DescribeStreamShardPaginationCoversAllShards
{
    private const string CheckId = "describe-stream-shard-pagination-covers-all-shards";
    private static readonly StreamViewType View = StreamViewType.NEW_AND_OLD_IMAGES;

    /// <summary>
    /// DescribeStream with Limit=1 pages one shard at a time, its token is the last shard, and the
    /// final page omits it; the pages cover the full shard set once. ListStreams is scoped by table.
    /// </summary>
    [Test]
    [DisplayName("dynamodbstreams.describe-stream-shard-pagination-covers-all-shards")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var streams = ctx.DynamoStreams;

        var (table, streamArn) = await CreateStreamTable(ctx, ddb, View, "sim2real-behav-dbspage-");
        var (desc, shardIds, _) = await WaitStreamUsable(streams, streamArn);

        Ensure(
            desc.StreamArn == streamArn,
            $"DescribeStream must echo the ARN it was asked about, got {Py.Repr(desc.StreamArn)}");
        Ensure(
            desc.TableName == table,
            $"DescribeStream must name the source table, got {Py.Repr(desc.TableName)}");
        Ensure(
            desc.StreamStatus?.Value == "ENABLED",
            "a stream that hands out iterators must report ENABLED, got "
            + $"{Py.Repr(desc.StreamStatus?.Value)}");
        Ensure(
            desc.StreamViewType?.Value == View.Value,
            $"StreamViewType must be {View.Value}, got {Py.Repr(desc.StreamViewType?.Value)}");
        var keySchema = (desc.KeySchema ?? [])
            .Select(k => new Dictionary<string, object?>(StringComparer.Ordinal) { ["AttributeName"] = k.AttributeName, ["KeyType"] = k.KeyType?.Value })
            .ToList();
        Ensure(
            keySchema.Count == 1 && keySchema[0]["AttributeName"] as string == "pk" && keySchema[0]["KeyType"] as string == "HASH",
            $"DescribeStream must echo the table key schema, got {Py.Repr(keySchema)}");
        Ensure(
            streamArn.EndsWith("/" + desc.StreamLabel, StringComparison.Ordinal),
            "the StreamLabel must be the trailing component of the stream ARN; label "
            + $"{Py.Repr(desc.StreamLabel)} vs arn {Py.Repr(streamArn)}");
        Ensure(
            desc.LastEvaluatedShardId is null,
            "an unlimited DescribeStream returned every shard, so it must not offer a page token; "
            + $"got {Py.Repr(desc.LastEvaluatedShardId)}");
        // An OPEN shard has a StartingSequenceNumber and no EndingSequenceNumber.
        foreach (var shard in desc.Shards)
        {
            var rng = shard.SequenceNumberRange;
            var start0 = rng.StartingSequenceNumber ?? "";
            Ensure(
                start0.Length > 0 && start0.All(char.IsAsciiDigit),
                $"every shard reports a numeric StartingSequenceNumber, got {ReprRange(rng)}");
            Ensure(
                rng.EndingSequenceNumber is null,
                $"an OPEN shard has no EndingSequenceNumber; shard {shard.ShardId} reported "
                + $"{Py.Repr(rng.EndingSequenceNumber)}");
        }

        // Bounded above the shard count so a token that never ends cannot loop forever.
        var paged = new List<string>();
        var tokens = new List<string?>();
        string? start = null;
        for (var page = 0; page < shardIds.Count + 2; page++)
        {
            var request = new Streams.DescribeStreamRequest { StreamArn = streamArn, Limit = 1 };
            if (start is not null)
            {
                request.ExclusiveStartShardId = start;
            }

            var pageDesc = (await streams.DescribeStreamAsync(request)).StreamDescription;
            var ids = (pageDesc.Shards ?? []).Select(s => s.ShardId).ToList();
            Ensure(
                ids.Count == 1,
                $"Limit=1 must return exactly one shard per page, got {ids.Count}: {Py.Repr(ids)}");
            paged.AddRange(ids);
            var token = pageDesc.LastEvaluatedShardId;
            tokens.Add(token);
            if (token is null)
            {
                break;
            }

            Ensure(
                token == ids[0],
                $"the page token must be the last shard on the page; page returned {Py.Repr(ids)} with "
                + $"token {Py.Repr(token)}");
            start = token;
        }

        Ensure(
            tokens[^1] is null,
            $"paging must terminate by omitting LastEvaluatedShardId; the last {tokens.Count} pages "
            + $"produced tokens {Py.Repr(tokens)}");
        Ensure(
            paged.Count == paged.Distinct(StringComparer.Ordinal).Count(),
            $"paging must not return a shard twice; the pages returned {Py.Repr(paged)}");
        Ensure(
            paged.ToHashSet(StringComparer.Ordinal).SetEquals(shardIds),
            "paging with Limit=1 must cover exactly the shards the unpaginated call reported; "
            + $"paged {Py.Repr(paged.Order(StringComparer.Ordinal).ToList())} vs full {Py.Repr(shardIds.Order(StringComparer.Ordinal).ToList())}");

        var listed = await ListStreams(streams, table);
        var listedArns = listed.Select(s => s.StreamArn).ToList();
        Ensure(
            listedArns.Count == 1 && listedArns[0] == streamArn,
            $"ListStreams(TableName={table}) must report exactly this table's one stream, got "
            + $"{Py.Repr(listedArns)}");
        Ensure(
            listed[0].TableName == table,
            $"the ListStreams entry must name the table, got {Repr(listed[0])}");
        Ensure(
            listed[0].StreamLabel == desc.StreamLabel,
            $"ListStreams and DescribeStream must agree on the StreamLabel; {Repr(listed[0])} vs "
            + $"{Py.Repr(desc.StreamLabel)}");

        var other = await ListStreams(streams, table + "-absent");
        Ensure(
            other.Count == 0,
            $"ListStreams for a table that does not exist must report no streams, got {Py.Repr(other.Select(Repr).ToList())}");
    }

    private static string Repr(Streams.StreamSummary entry) =>
        Py.Repr(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["StreamArn"] = entry.StreamArn,
            ["TableName"] = entry.TableName,
            ["StreamLabel"] = entry.StreamLabel,
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

    /// <summary>
    /// Every stream for one table, following LastEvaluatedStreamArn to the end.
    /// ListStreams exposes no paginator in botocore, hence the explicit next-token loop.
    /// </summary>
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
}
