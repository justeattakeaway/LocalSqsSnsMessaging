// Translated from behavioral/dynamodb/check_tag_resource_exceeds_tag_limit_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.DynamoDb;

/// <summary>dynamodb.tag-resource-exceeds-tag-limit-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TagResourceExceedsTagLimitRejected
{
    /// <summary>
    /// A single TagResource call with 51 tags (Tags max-list-length is 50) is rejected wholesale with
    /// ValidationException and applies none of them.
    /// </summary>
    [Test]
    [DisplayName("dynamodb.tag-resource-exceeds-tag-limit-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ddb = ctx.Dynamo;
        var tableName = FidelityContext.UniqueName("sim2real-behav-ddb-");
        var created = await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = tableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        });
        ctx.OnCleanup(() => SafeDeleteTable(ddb, tableName));
        var tableArn = created.TableDescription.TableArn;
        await WaitTableActive(ddb, tableName);

        async Task<List<Tag>> ListTags() => (await ddb.ListTagsOfResourceAsync(new ListTagsOfResourceRequest { ResourceArn = tableArn })).Tags ?? [];

        // ListTagsOfResource is eventually consistent with TagResource, so the
        // confirming read is a bounded poll, not a single point-in-time read.
        var controlTag = new Tag { Key = "sim2real-control", Value = "v" };
        await ddb.TagResourceAsync(new TagResourceRequest { ResourceArn = tableArn, Tags = [controlTag] });
        var controlTags = await PollUntil(
            ListTags,
            t => IsOnly(t, controlTag),
            timeoutSeconds: 30.0,
            description: "control tag visible via ListTagsOfResource");
        Ensure(IsOnly(controlTags, controlTag), $"expected the control tag to attach, found {Repr(controlTags)}");

        // A single call is used because a cumulative 51st tag is enforced only after quota
        // accounting propagates, with no bounded convergence.
        var overLimitTags = Enumerable.Range(0, 51).Select(i => new Tag { Key = $"sim2real-k{i}", Value = $"v{i}" }).ToList();
        var (code, message) = await Error(() => ddb.TagResourceAsync(new TagResourceRequest { ResourceArn = tableArn, Tags = overLimitTags }));
        Ensure(code == "ValidationException", $"expected ValidationException, got {Py.Repr(code)}");
        const string expected = "Number of Tags exceed the current limit for the provided ResourceArn";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        var tags = await ListTags();
        Ensure(IsOnly(tags, controlTag), $"expected only the control tag to survive the rejected request, found {Repr(tags)}");
    }

    private static bool IsOnly(List<Tag> tags, Tag tag) => tags.Count == 1 && tags[0].Key == tag.Key && tags[0].Value == tag.Value;

    private static string Repr(List<Tag>? tags) =>
        Py.Repr(tags?.Select(t => new Dictionary<string, string>(StringComparer.Ordinal) { ["Key"] = t.Key, ["Value"] = t.Value }).ToList());

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutSeconds = 30.0, double intervalSeconds = 2.0, string description = "condition")
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

        throw Fail($"{description} not observed within {Py.Repr(timeoutSeconds)}s; last saw {Py.Repr(last)}");
    }

    /// <summary>(code, message) of the ClientError fn() raises; RAISES if the call unexpectedly succeeds.</summary>
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

    private static async Task<TableDescription> WaitTableActive(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 120.0, double intervalSeconds = 2.0)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var desc = (await ddb.DescribeTableAsync(tableName)).Table;
            if (desc.TableStatus == TableStatus.ACTIVE)
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"table {tableName} never became ACTIVE");
    }

    /// <summary>DeleteTable, retried while a just-applied TagResource still holds the table in use.</summary>
    private static async Task SafeDeleteTable(IAmazonDynamoDB ddb, string tableName, double timeoutSeconds = 60.0, double intervalSeconds = 3.0)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => ddb.DeleteTableAsync(tableName));
            if (error is null || error.Code == "ResourceNotFoundException")
            {
                return;
            }

            if (error.Code != "ResourceInUseException" || clock.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }
    }
}
