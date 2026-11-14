// Translated from behavioral/sns/check_display_name_over_100_characters_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.display-name-over-100-characters-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DisplayNameOver100CharactersRejected
{
    private const string Check = "display-name-over-100-characters-rejected";

    /// <summary>
    /// SetTopicAttributes refuses a 101-character DisplayName and keeps the earlier value, while
    /// a 100-character DisplayName, the documented maximum, is accepted and read back.
    /// </summary>
    [Test]
    [DisplayName("sns.display-name-over-100-characters-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsTopicAttributes, "SetTopicAttributes accepts a 101-character DisplayName instead of refusing it with InvalidParameter 'Invalid parameter: DisplayName'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-longdn-"),
            Attributes = new() { ["DisplayName"] = "before" },
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));

        var (code, message) = await Error(() => sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = arn, AttributeName = "DisplayName", AttributeValue = new string('d', 101),
        }));
        Ensure(code == "InvalidParameter", $"a 101-character DisplayName gave '{code}'");
        Ensure(message == "Invalid parameter: DisplayName", $"unexpected message for a 101-character DisplayName: '{message}'");
        Ensure(await Display(sns, arn) == "before", $"the refused write must keep DisplayName, got '{await Display(sns, arn)}'");

        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = arn, AttributeName = "DisplayName", AttributeValue = new string('d', 100),
        });
        Ensure(await Display(sns, arn) == new string('d', 100), "a 100-character DisplayName must be stored");
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

    private static async Task<string?> Display(AmazonSimpleNotificationServiceClient sns, string arn)
        => (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes?.GetValueOrDefault("DisplayName");
}
