// Translated from behavioral/sns/check_create_topic_idempotent_and_conflict.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.create-topic-idempotent-and-conflict</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTopicIdempotentAndConflict
{
    private const string Check = "create-topic-idempotent-and-conflict";

    /// <summary>CreateTopic is idempotent for identical name+attributes but rejects a name reused with different attributes.</summary>
    [Test]
    [DisplayName("sns.create-topic-idempotent-and-conflict")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsTopicAttributes, "CreateTopic on an existing name with a different DisplayName succeeds instead of InvalidParameter 'Topic already exists with different attributes'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-topic-idem-");
        List<Tag> tagsKv = [new() { Key = "sim2real", Value = Check }, new() { Key = "sim2real:managed", Value = "true" }];
        var resp1 = await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = new() { ["DisplayName"] = "first" }, Tags = tagsKv });
        var arn1 = resp1.TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn1 }));

        var resp2 = await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = new() { ["DisplayName"] = "first" } });
        Ensure(resp2.TopicArn == arn1, "re-creating with identical attributes must return the same TopicArn");

        var (code, message) = await Error(() => sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = new() { ["DisplayName"] = "different" } }));
        Ensure(code == "InvalidParameter", $"expected InvalidParameter for name reused with different attributes, got {code}");
        Ensure(message == "Invalid parameter: Attributes Reason: Topic already exists with different attributes",
            $"unexpected InvalidParameter message for a name reused with different attributes: '{message}'");

        var attrs = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn1 })).Attributes;
        Ensure(attrs["DisplayName"] == "first",
            "rejected conflicting CreateTopic must not mutate the existing topic's attributes, " +
            $"got DisplayName='{attrs.GetValueOrDefault("DisplayName")}'");

        await sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn1 });
        var resp3 = await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = new() { ["DisplayName"] = "different" } });
        Ensure(resp3.TopicArn == arn1, $"freeing the conflicting name should let it be recreated at the same ARN, got {resp3.TopicArn}");
        var attrs3 = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn1 })).Attributes;
        Ensure(attrs3["DisplayName"] == "different",
            "once the conflicting name is freed, the identical call that was rejected must now succeed " +
            $"with its own attributes, got DisplayName='{attrs3.GetValueOrDefault("DisplayName")}'");
    }

    /// <summary>
    /// Call fn(); return botocore (Error.Code, Error.Message) of the ClientError it raises. An
    /// unexpected SUCCESS raises, so an over-permissive target cannot read as a code mismatch.
    /// </summary>
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
}
