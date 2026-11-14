// Translated from behavioral/sns/check_tag_resource_reserved_aws_prefix_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.tag-resource-reserved-aws-prefix-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TagResourceReservedAwsPrefixRejected
{
    private const string Check = "tag-resource-reserved-aws-prefix-rejected";

    /// <summary>Return (Error.Code, Error.Message) of the ClientError fn raises; a success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task<Dictionary<string, string>> Tags(AmazonSimpleNotificationServiceClient sns, string arn) =>
        ((await sns.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = arn })).Tags ?? []).ToDictionary(t => t.Key, t => t.Value);

    private static bool Same(Dictionary<string, string> actual, Dictionary<string, string> expected) =>
        actual.Count == expected.Count && expected.All(kv => actual.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static string Show(Dictionary<string, string> d) => "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";

    /// <summary>
    /// TagResource refuses a key in the reserved `aws:` namespace and applies nothing from
    /// that request -- not even its legal sibling tag -- while the sibling alone is accepted.
    /// </summary>
    [Test]
    [DisplayName("sns.tag-resource-reserved-aws-prefix-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-awstag-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));

        var (code, message) = await Error(() => sns.TagResourceAsync(new TagResourceRequest
        {
            ResourceArn = arn,
            Tags = [new() { Key = "aws:reserved", Value = "x" }, new() { Key = "legal", Value = "y" }],
        }));
        Ensure(code == "InvalidParameter", $"TagResource with an aws: key gave '{code}'");
        Ensure(message == "Invalid parameter: Tags Reason: Caller is an end user and not allowed to mutate system tags", $"unexpected message for an aws: tag key: '{message}'");
        Ensure(Same(await Tags(sns, arn), new() { ["sim2real"] = Check }),
            $"the refused TagResource must apply nothing, got {Show(await Tags(sns, arn))}");

        await sns.TagResourceAsync(new TagResourceRequest { ResourceArn = arn, Tags = [new() { Key = "legal", Value = "y" }] });
        Ensure(Same(await Tags(sns, arn), new() { ["sim2real"] = Check, ["legal"] = "y" }),
            $"the legal tag alone must be accepted, got {Show(await Tags(sns, arn))}");
    }
}
