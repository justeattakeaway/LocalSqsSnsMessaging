// Translated from behavioral/sns/check_op_create_topic_invalid_name_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.op-create-topic-invalid-name-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpCreateTopicInvalidNameRejected
{
    private const string Check = "op-create-topic-invalid-name-rejected";

    /// <summary>
    /// CreateTopic must reject names containing characters outside [A-Za-z0-9_-] and names over 256 chars, and must not create anything as a side effect.
    /// </summary>
    [Test]
    [DisplayName("sns.op-create-topic-invalid-name-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsValidation, "CreateTopic accepts a name with characters outside [A-Za-z0-9_-] ('bad name!<hex>$') instead of refusing with InvalidParameter 'Invalid parameter: Topic Name'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;

        // Positive control: the same operation, a valid name, succeeds -- so the two
        // rejections below prove something about invalid names, not merely that every
        // CreateTopic call fails in this account.
        var goodName = FidelityContext.UniqueName("sim2real-behav-sns-validname-");
        var goodArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = goodName,
            Tags = [new() { Key = "sim2real-check", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = goodArn }));
        var attrs = await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = goodArn });
        Ensure(attrs.Attributes["TopicArn"] == goodArn,
            $"positive control: GetTopicAttributes did not resolve the created topic '{goodArn}'");

        var badCharName = FidelityContext.UniqueName("bad name!") + "$";
        var (code, message) = await Error(() => sns.CreateTopicAsync(new CreateTopicRequest { Name = badCharName }));
        Ensure(code == "InvalidParameter", $"expected InvalidParameter for an illegal topic name, got {code}");
        var expected = "Invalid parameter: Topic Name";
        Ensure(message == expected, $"expected message '{expected}', got '{message}'");

        // 257-character name exceeds the documented 256-char limit.
        var longName = new string('a', 257);
        (code, message) = await Error(() => sns.CreateTopicAsync(new CreateTopicRequest { Name = longName }));
        Ensure(code == "InvalidParameter", $"expected InvalidParameter for an over-long topic name, got {code}");
        expected = "Invalid parameter: Topic Name";
        Ensure(message == expected, $"expected message '{expected}', got '{message}'");

        // Effect absent: an independent read at the destination shows neither rejected
        // name was created as a side effect of the refused calls.
        var names = await TopicNames(sns);
        Ensure(!names.Contains(badCharName), $"rejected topic name '{badCharName}' appears in ListTopics");
        Ensure(!names.Contains(longName), "rejected over-long topic name appears in ListTopics");
    }

    /// <summary>
    /// Call fn(); return (code, message) of the ClientError it raises. An unexpected success
    /// raises, so a target that accepts what AWS refuses never reads as a code mismatch.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded and returned {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task<HashSet<string>> TopicNames(AmazonSimpleNotificationServiceClient sns)
    {
        var names = new HashSet<string>();
        await foreach (var page in sns.Paginators.ListTopics(new ListTopicsRequest()).Responses)
        {
            foreach (var t in page.Topics ?? [])
            {
                names.Add(t.TopicArn[(t.TopicArn.LastIndexOf(':') + 1)..]);
            }
        }

        return names;
    }
}
