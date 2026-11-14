// Translated from behavioral/sns/check_fifo_topic_name_suffix_required.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.fifo-topic-name-suffix-required</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoTopicNameSuffixRequired
{
    private const string Scenario = "sns-fifo-topic-name-suffix";

    /// <summary>
    /// Call fn(); return (code, message) of the ClientError it raises. An unexpected success
    /// raises, so a target that accepts what AWS refuses never reads as a code mismatch.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task<CreateTopicResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded and returned {{'TopicArn': '{result!.TopicArn}'}}");
        }

        return (error.Code, error.Message);
    }

    private static List<Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>Creating a FIFO topic (Attributes.FifoTopic=true) without a .fifo name suffix must fail.</summary>
    [Test]
    [DisplayName("sns.fifo-topic-name-suffix-required")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsValidation, "CreateTopic with FifoTopic=true and no .fifo suffix succeeds instead of failing InvalidParameter 'Invalid parameter: Topic Name'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;

        // Positive control: a correctly-suffixed FIFO topic must succeed, so the rejection
        // below is attributable to the missing suffix and not to FIFO being unavailable at all.
        var goodName = FidelityContext.UniqueName("sim2real-behav-sns-fifo-good-") + ".fifo";
        var goodArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = goodName,
            Attributes = new() { ["FifoTopic"] = "true" },
            Tags = TagsKv(),
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = goodArn }));
        Ensure(goodArn.EndsWith(goodName, StringComparison.Ordinal),
            $"expected the positive-control FIFO topic ARN to echo '{goodName}', got {goodArn}");

        var badName = FidelityContext.UniqueName("sim2real-behav-sns-fifo-bad-");
        var (code, message) = await Error(() => sns.CreateTopicAsync(new CreateTopicRequest { Name = badName, Attributes = new() { ["FifoTopic"] = "true" } }));
        Ensure(code == "InvalidParameter", $"expected InvalidParameter for FIFO topic missing .fifo suffix, got {code}");
        const string expected = "Invalid parameter: Topic Name";
        Ensure(message == expected, $"expected message '{expected}', got '{message}'");

        // Effect absent: the rejected topic must not have been created at the destination.
        var arns = ((await sns.ListTopicsAsync(new ListTopicsRequest())).Topics ?? []).Select(t => t.TopicArn).ToList();
        Ensure(!arns.Any(a => a.EndsWith($":{badName}", StringComparison.Ordinal)),
            $"rejected FIFO topic '{badName}' must not appear in ListTopics, saw [{string.Join(", ", arns.Select(a => $"'{a}'"))}]");
    }
}
