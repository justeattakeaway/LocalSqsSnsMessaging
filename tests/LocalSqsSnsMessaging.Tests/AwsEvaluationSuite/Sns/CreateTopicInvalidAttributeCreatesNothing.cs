// Translated from behavioral/sns/check_create_topic_invalid_attribute_creates_nothing.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.create-topic-invalid-attribute-creates-nothing</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateTopicInvalidAttributeCreatesNothing
{
    private const string Check = "create-topic-invalid-attribute-creates-nothing";

    /// <summary>
    /// CreateTopic with a malformed Policy is refused and creates no topic (GetTopicAttributes on
    /// its ARN is NotFound), while the same name without attributes then creates normally.
    /// </summary>
    [Test]
    [DisplayName("sns.create-topic-invalid-attribute-creates-nothing")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-badcreate-");
        var arn = $"arn:aws:sns:{ctx.Region}:{ctx.Account}:{name}";
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));

        var (code, message) = await Error(() => sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = new() { ["Policy"] = "{not json" } }));
        Ensure(code == "InvalidParameter", $"CreateTopic with a malformed Policy gave '{code}'");
        Ensure(message == "Invalid parameter: Policy Error: null", $"unexpected message for a malformed Policy at create: '{message}'");
        (code, message) = await Error(() => sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn }));
        Ensure(code == "NotFound", $"the refused CreateTopic must create nothing, GetTopicAttributes gave '{code}'");
        Ensure(message == "Topic does not exist", $"unexpected NotFound message: '{message}'");

        var created = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Tags = [new() { Key = "sim2real", Value = Check }] })).TopicArn;
        Ensure(created == arn, "the same name without the bad attribute must create the topic");
        Ensure((await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes["TopicArn"] == arn,
            "assert sns.get_topic_attributes(TopicArn=arn)[\"Attributes\"][\"TopicArn\"] == arn");
    }

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
}
