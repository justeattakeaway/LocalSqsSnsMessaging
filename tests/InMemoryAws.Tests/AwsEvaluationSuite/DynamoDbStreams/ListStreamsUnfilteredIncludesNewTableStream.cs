// Translated from behavioral/dynamodbstreams/check_list_streams_unfiltered_includes_new_table_stream.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using Streams = Amazon.DynamoDBStreams.Model;

namespace InMemoryAws.Tests.AwsEvaluationSuite.DynamoDbStreams;

/// <summary>dynamodbstreams.list-streams-unfiltered-includes-new-table-stream</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListStreamsUnfilteredIncludesNewTableStream
{
    private const string CheckId = "list-streams-unfiltered-includes-new-table-stream";

    /// <summary>
    /// ListStreams with no TableName lists the account's streams; paging with
    /// ExclusiveStartStreamArn surfaces an entry whose StreamArn matches DescribeTable's
    /// LatestStreamArn and names the new table.
    /// </summary>
    [Test]
    [DisplayName("dynamodbstreams.list-streams-unfiltered-includes-new-table-stream")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var streams = ctx.DynamoStreams;

        var table = FidelityContext.UniqueName("sim2real-behav-lsunf-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = table,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.KEYS_ONLY },
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => ddb.DeleteTableAsync(table));
        await ddb.WaitForTableExists(table, delaySeconds: 2, maxAttempts: 250);

        var described = (await ddb.DescribeTableAsync(table)).Table;
        var streamArn = described.LatestStreamArn;
        var label = described.LatestStreamLabel;
        var wantArn = $"{described.TableArn}/stream/{label}";
        Ensure(streamArn == wantArn, $"DescribeTable LatestStreamArn is {Py.Repr(streamArn)}, expected {Py.Repr(wantArn)}");

        var entry = await FindUnfiltered(streams, streamArn);
        Ensure(
            entry is not null,
            $"an unfiltered ListStreams must, across its pages, surface an entry for {Py.Repr(streamArn)}");
        Ensure(
            entry.TableName == table,
            $"the ListStreams entry for {Py.Repr(streamArn)} must name the table, got {Repr(entry)}");
        Ensure(
            entry.StreamLabel == label,
            $"the entry's StreamLabel must be DescribeTable's LatestStreamLabel {Py.Repr(label)}, got {Repr(entry)}");
    }

    private static string Repr(Streams.StreamSummary entry) =>
        Py.Repr(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["StreamArn"] = entry.StreamArn,
            ["TableName"] = entry.TableName,
            ["StreamLabel"] = entry.StreamLabel,
        });

    /// <summary>
    /// The ListStreams entry for <paramref name="streamArn"/>, found with no TableName filter.
    /// Walks every page via ExclusiveStartStreamArn/LastEvaluatedStreamArn; retried over a bounded
    /// window because other tables' streams can be created or deleted between pages.
    /// </summary>
    private static async Task<Streams.StreamSummary?> FindUnfiltered(
        Amazon.DynamoDBStreams.IAmazonDynamoDBStreams streams,
        string streamArn,
        double timeoutS = 90.0,
        int pageLimit = 100,
        int maxPages = 500)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            string? start = null;
            for (var page = 0; page < maxPages; page++)
            {
                var request = new Streams.ListStreamsRequest { Limit = pageLimit };
                if (start is not null)
                {
                    request.ExclusiveStartStreamArn = start;
                }

                var resp = await streams.ListStreamsAsync(request);
                foreach (var entry in resp.Streams ?? [])
                {
                    if (entry.StreamArn == streamArn)
                    {
                        return entry;
                    }
                }

                start = resp.LastEvaluatedStreamArn;
                if (start is null)
                {
                    break;
                }
            }

            if (clock.Elapsed.TotalSeconds >= timeoutS)
            {
                return null;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }
    }
}
