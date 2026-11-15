// Translated from behavioral/dynamodb/check_stream_arn_tag_list_untag_independent_of_table.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.stream-arn-tag-list-untag-independent-of-table</summary>
[Category("AwsEvaluationSuite")]
public sealed class StreamArnTagListUntagIndependentOfTable
{
    private const string CheckId = "stream-arn-tag-list-untag-independent-of-table";
    private const double CleanupS = 250.0; // under the suite's 300 s cleanup budget

    /// <summary>Run a tagging call, retried while a previous tag update on the table is still settling.</summary>
    private static async Task<T> TagOp<T>(Func<Task<T>> fn)
        where T : class
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var (result, error) = await FidelityContext.Capture(fn);
            if (error is null)
            {
                return result!;
            }

            if (error.Code != "LimitExceededException" || clock.Elapsed >= TimeSpan.FromSeconds(120.0))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }
    }

    private static async Task<Dictionary<string, string>> TagsOf(IAmazonDynamoDB ddb, string arn) =>
        ((await TagOp(() => ddb.ListTagsOfResourceAsync(new ListTagsOfResourceRequest { ResourceArn = arn }))).Tags ?? [])
            .ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS = 60, double intervalS = 2.0, string description = "condition")
    {
        var clock = FidelityTime.StartNew();
        T last = default!;
        while (clock.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not met within {timeoutS}s. Last result: {Py.Repr(last)}");
    }

    /// <summary>Delete the table; retry ResourceInUseException while a tag update settles, gone is done.</summary>
    private static async Task DeleteTable(IAmazonDynamoDB ddb, string tableName)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed >= TimeSpan.FromSeconds(CleanupS))
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static Dictionary<string, string> Tags(params (string Key, string Value)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

    /// <summary>
    /// TagResource/ListTagsOfResource/UntagResource accept a table's stream ARN, and the tags they
    /// manage there stay entirely separate from the table's own tags, in both directions.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.stream-arn-tag-list-untag-independent-of-table")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-strmtag-");
        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = new StreamSpecification { StreamEnabled = true, StreamViewType = StreamViewType.NEW_IMAGE },
            Tags = [new Tag { Key = "sim2real", Value = CheckId }],
        });
        ctx.OnCleanup(() => DeleteTable(ddb, tableName));
        await ddb.WaitForTableExists(tableName, delaySeconds: 2, maxAttempts: 250);
        var desc = (await ddb.DescribeTableAsync(tableName)).Table;
        var (tableArn, streamArn) = (desc.TableArn, desc.LatestStreamArn);
        Ensure(
            streamArn.StartsWith(tableArn + "/stream/", StringComparison.Ordinal),
            $"the stream ARN {Py.Repr(streamArn)} is not nested under the table ARN {Py.Repr(tableArn)}");

        await TagOp(() => ddb.TagResourceAsync(new TagResourceRequest { ResourceArn = tableArn, Tags = [new Tag { Key = "scope", Value = "table-only" }] }));
        var tableTags = await PollUntil(
            () => TagsOf(ddb, tableArn),
            d => d.GetValueOrDefault("scope") == "table-only",
            description: "table tag visible on ListTagsOfResource(table ARN)");
        Ensure(
            Py.Equal(tableTags, Tags(("sim2real", CheckId), ("scope", "table-only"))),
            $"expected only the create-time and table-only tags on the table, got {Py.Repr(tableTags)}");

        await TagOp(() => ddb.TagResourceAsync(new TagResourceRequest { ResourceArn = streamArn, Tags = [new Tag { Key = "scope", Value = "stream-only" }] }));
        var streamTags = await PollUntil(
            () => TagsOf(ddb, streamArn),
            d => d.GetValueOrDefault("scope") == "stream-only",
            description: "stream tag visible on ListTagsOfResource(stream ARN)");
        Ensure(
            Py.Equal(streamTags, Tags(("scope", "stream-only"))),
            $"expected ListTagsOfResource(stream ARN) to carry only the stream's own tag, got {Py.Repr(streamTags)}");
        var tableTagsUnaffected = await TagsOf(ddb, tableArn);
        Ensure(
            Py.Equal(tableTagsUnaffected, Tags(("sim2real", CheckId), ("scope", "table-only"))),
            $"tagging the stream changed ListTagsOfResource(table ARN) to {Py.Repr(tableTagsUnaffected)}");

        await TagOp(() => ddb.UntagResourceAsync(new UntagResourceRequest { ResourceArn = streamArn, TagKeys = ["scope"] }));
        var streamTagsAfter = await PollUntil(
            () => TagsOf(ddb, streamArn),
            d => !d.ContainsKey("scope"),
            description: "stream tag removed from ListTagsOfResource(stream ARN)");
        Ensure(streamTagsAfter.Count == 0, $"expected the stream's tags empty after UntagResource(stream ARN), got {Py.Repr(streamTagsAfter)}");
        var tableTagsFinal = await TagsOf(ddb, tableArn);
        Ensure(
            Py.Equal(tableTagsFinal, Tags(("sim2real", CheckId), ("scope", "table-only"))),
            $"untagging the stream changed ListTagsOfResource(table ARN) to {Py.Repr(tableTagsFinal)}");
    }
}
