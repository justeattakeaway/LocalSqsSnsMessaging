// Translated from behavioral/sns/check_topic_feedback_sample_rate_out_of_range_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.topic-feedback-sample-rate-out-of-range-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TopicFeedbackSampleRateOutOfRangeRejected
{
    private const string Check = "topic-feedback-sample-rate-out-of-range-rejected";

    /// <summary>
    /// SQSSuccessFeedbackSampleRate outside [0, 100], or not a number, is refused InvalidParameter
    /// and leaves the attribute unset (not clamped), while the boundary 100 is accepted and read back.
    /// </summary>
    [Test]
    [DisplayName("sns.topic-feedback-sample-rate-out-of-range-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsTopicAttributes, "SetTopicAttributes accepts SQSSuccessFeedbackSampleRate=101 instead of refusing it with InvalidParameter")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-fbrate-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));

        foreach (var bad in new[] { "101", "-1", "abc" })
        {
            var (code, message) = await Error(() => sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
            {
                TopicArn = arn, AttributeName = "SQSSuccessFeedbackSampleRate", AttributeValue = bad,
            }));
            Ensure(code == "InvalidParameter",
                $"a SQSSuccessFeedbackSampleRate of '{bad}' must be refused InvalidParameter, got '{code}': '{message}'");
            var expectedMessage = $"Invalid parameter: SQSSuccessFeedbackSampleRate: {bad} value provided is not an " +
                "integer between 0-100";
            Ensure(message == expectedMessage, $"expected the exact message '{expectedMessage}' for '{bad}', got '{message}'");
            var still = await Attrs(sns, arn);
            Ensure(!still.ContainsKey("SQSSuccessFeedbackSampleRate"),
                $"a rejected sample rate must leave the attribute unset, got '{still.GetValueOrDefault("SQSSuccessFeedbackSampleRate")}'");
        }

        // Positive control: the boundary value itself is accepted.
        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = arn, AttributeName = "SQSSuccessFeedbackSampleRate", AttributeValue = "100",
        });
        var final = await Attrs(sns, arn);
        Ensure(final.GetValueOrDefault("SQSSuccessFeedbackSampleRate") == "100",
            $"a sample rate of 100 must be accepted and read back exactly, got '{final.GetValueOrDefault("SQSSuccessFeedbackSampleRate")}'");
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

    private static async Task<Dictionary<string, string>> Attrs(AmazonSimpleNotificationServiceClient sns, string arn)
        => (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes ?? [];
}
