// Translated from behavioral/dynamodb/check_tag_resource_nonexistent_stream_arn_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.tag-resource-nonexistent-stream-arn-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TagResourceNonexistentStreamArnRejected
{
    private const string CheckId = "tag-resource-nonexistent-stream-arn-rejected";

    /// <summary>Under the suite's 300 s cleanup budget.</summary>
    private const double CleanupSeconds = 250.0;

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

            if (error.Code != "LimitExceededException" || clock.Elapsed.TotalSeconds >= 120.0)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }
    }

    private static async Task<Dictionary<string, string>> TagsOf(IAmazonDynamoDB ddb, string arn) =>
        ((await TagOp(() => ddb.ListTagsOfResourceAsync(new ListTagsOfResourceRequest { ResourceArn = arn }))).Tags ?? [])
            .ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, int timeoutSeconds = 60, double intervalSeconds = 2.0, string description = "condition")
        where T : class
    {
        var clock = FidelityTime.StartNew();
        T? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"{description} not met within {timeoutSeconds}s. Last result: {Py.Repr(last)}");
    }

    /// <summary>(code, message) of the ClientError fn() raises; raises if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be rejected, but it succeeded: {result}");
    }

    private static async Task ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (gotCode, gotMessage) = await Error(fn);
        Ensure(gotCode == code, $"expected {Py.Repr(code)}, got {Py.Repr(gotCode)} ({Py.Repr(gotMessage)})");
        Ensure(gotMessage == message, $"unexpected message {Py.Repr(gotMessage)}");
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

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= CleanupSeconds)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// TagResource and ListTagsOfResource on a well-formed but nonexistent stream ARN under a real
    /// table raise ResourceNotFoundException naming that ARN, while the table's real stream ARN
    /// accepts a tag.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.tag-resource-nonexistent-stream-arn-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-strmtag404-");
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

        // Positive control: the real stream ARN accepts a tag.
        await TagOp(() => ddb.TagResourceAsync(new TagResourceRequest { ResourceArn = streamArn, Tags = [new Tag { Key = "scope", Value = "stream" }] }));
        var streamTags = await PollUntil(
            () => TagsOf(ddb, streamArn),
            d => d.GetValueOrDefault("scope") == "stream",
            description: "positive-control stream tag visible");
        var expectedTags = new Dictionary<string, string>(StringComparer.Ordinal) { ["scope"] = "stream" };
        Ensure(Py.Equal(streamTags, expectedTags), $"expected the positive-control tag, got {Py.Repr(streamTags)}");

        var fakeStreamArn = tableArn + "/stream/2000-01-01T00:00:00.000";
        const string notFoundCode = "ResourceNotFoundException";
        var notFoundMessage = $"Requested resource not found: ResourceArn: {fakeStreamArn} not found";
        await ExpectError(
            () => TagOp(() => ddb.TagResourceAsync(new TagResourceRequest { ResourceArn = fakeStreamArn, Tags = [new Tag { Key = "k", Value = "v" }] })),
            notFoundCode,
            notFoundMessage);
        await ExpectError(
            () => TagOp(() => ddb.ListTagsOfResourceAsync(new ListTagsOfResourceRequest { ResourceArn = fakeStreamArn })),
            notFoundCode,
            notFoundMessage);

        // The rejected calls touched nothing: the real stream's tags are unchanged.
        var streamTagsAfter = await TagsOf(ddb, streamArn);
        Ensure(
            Py.Equal(streamTagsAfter, expectedTags),
            $"expected the real stream's tags unchanged by the rejected calls, got {Py.Repr(streamTagsAfter)}");
    }
}
