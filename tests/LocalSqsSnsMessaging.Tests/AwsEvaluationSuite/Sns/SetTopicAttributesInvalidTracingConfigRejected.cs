// Translated from behavioral/sns/check_set_topic_attributes_invalid_tracing_config_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.set-topic-attributes-invalid-tracing-config-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SetTopicAttributesInvalidTracingConfigRejected
{
    private const string Check = "set-topic-attributes-invalid-tracing-config-rejected";

    /// <summary>
    /// SetTopicAttributes refuses TracingConfig=Sampled, a value outside its closed vocabulary,
    /// and stores nothing; the legal value Active is then accepted and read back.
    /// </summary>
    [Test]
    [DisplayName("sns.set-topic-attributes-invalid-tracing-config-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsTopicAttributes, "SetTopicAttributes accepts TracingConfig=Sampled instead of refusing with InvalidParameter 'Invalid parameter: Invalid tracing config value: Sampled'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-badtracingconfig-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        Ensure(await Value(sns, arn) is null, $"a new topic reports no TracingConfig until one is set, got '{await Value(sns, arn)}'");

        var (code, message) = await Error(() => sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = arn,
            AttributeName = "TracingConfig",
            AttributeValue = "Sampled",
        }));
        Ensure(code == "InvalidParameter", $"TracingConfig=Sampled gave '{code}'");
        Ensure(message == "Invalid parameter: Invalid tracing config value: Sampled", $"unexpected message for TracingConfig=Sampled: '{message}'");
        Ensure(await Value(sns, arn) is null, $"the refused write must store nothing, but TracingConfig reads '{await Value(sns, arn)}'");

        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest { TopicArn = arn, AttributeName = "TracingConfig", AttributeValue = "Active" });
        Ensure(await Value(sns, arn) == "Active", $"TracingConfig=Active must be stored, got '{await Value(sns, arn)}'");
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

    private static async Task<string?> Value(AmazonSimpleNotificationServiceClient sns, string arn) =>
        (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes?.GetValueOrDefault("TracingConfig");
}
