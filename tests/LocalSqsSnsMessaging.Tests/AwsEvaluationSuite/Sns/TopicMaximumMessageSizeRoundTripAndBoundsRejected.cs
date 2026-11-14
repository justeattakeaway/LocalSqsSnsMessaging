// Translated from behavioral/sns/check_topic_maximum_message_size_round_trip_and_bounds_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.topic-maximum-message-size-round-trip-and-bounds-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TopicMaximumMessageSizeRoundTripAndBoundsRejected
{
    private const string Check = "topic-maximum-message-size-round-trip-and-bounds-rejected";
    private const int Min = 1024;
    private const int Max = 1048576;

    /// <summary>
    /// MaximumMessageSize is absent until set and round-trips inside [1024, 1048576]; a value
    /// outside is refused InvalidParameter and leaves the last accepted value and DisplayName intact.
    /// </summary>
    [Test]
    [DisplayName("sns.topic-maximum-message-size-round-trip-and-bounds-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-maxmsg-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));

        var baseAttrs = await Attrs(sns, arn);
        Ensure(!baseAttrs.ContainsKey("MaximumMessageSize"),
            "a new topic must report no MaximumMessageSize until one is set, got " +
            $"'{baseAttrs.GetValueOrDefault("MaximumMessageSize")}'");

        var sibling = FidelityContext.UniqueName("sim2real-sns-maxmsg-display-");
        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest { TopicArn = arn, AttributeName = "DisplayName", AttributeValue = sibling });

        const int good = 300000;
        var goodText = good.ToString(CultureInfo.InvariantCulture);
        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest { TopicArn = arn, AttributeName = "MaximumMessageSize", AttributeValue = goodText });
        var got = await Attrs(sns, arn);
        Ensure(got.GetValueOrDefault("MaximumMessageSize") == goodText,
            $"MaximumMessageSize must read back as set; expected {good}, got " +
            $"'{got.GetValueOrDefault("MaximumMessageSize")}'");
        Ensure(got.GetValueOrDefault("DisplayName") == sibling,
            $"setting MaximumMessageSize must not disturb DisplayName; expected '{sibling}', got " +
            $"'{got.GetValueOrDefault("DisplayName")}'");

        foreach (var (bad, label) in new[] { (Min - 1, "below the minimum"), (Max + 1, "above the maximum") })
        {
            var (code, message) = await Error(() => sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
            {
                TopicArn = arn, AttributeName = "MaximumMessageSize", AttributeValue = bad.ToString(CultureInfo.InvariantCulture),
            }));
            Ensure(code == "InvalidParameter",
                $"a MaximumMessageSize of {bad} ({label}) must be refused InvalidParameter, got " +
                $"'{code}': '{message}'");
            var expectedMessage = $"Invalid parameter: MaximumMessageSize: {bad} is not an integer between " +
                $"{Min} and {Max} bytes";
            Ensure(message == expectedMessage,
                $"expected the exact message '{expectedMessage}' for {bad} ({label}), got " +
                $"'{message}'");
            var still = await Attrs(sns, arn);
            Ensure(still.GetValueOrDefault("MaximumMessageSize") == goodText,
                $"a rejected MaximumMessageSize write ({bad}, {label}) must leave the prior value " +
                $"{good} in place, got '{still.GetValueOrDefault("MaximumMessageSize")}'");
            Ensure(still.GetValueOrDefault("DisplayName") == sibling,
                $"a rejected MaximumMessageSize write ({bad}, {label}) must not disturb " +
                $"DisplayName either, got '{still.GetValueOrDefault("DisplayName")}'");
        }
    }

    private static async Task<Dictionary<string, string>> Attrs(AmazonSimpleNotificationServiceClient sns, string arn) =>
        (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes ?? [];

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
