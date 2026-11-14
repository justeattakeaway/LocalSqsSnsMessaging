// Translated from behavioral/sns/check_fifo_throughput_scope_on_a_standard_topic_or_an_unknown_value_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.fifo-throughput-scope-on-a-standard-topic-or-an-unknown-value-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoThroughputScopeOnAStandardTopicOrAnUnknownValueRejected
{
    private const string Check = "fifo-throughput-scope-on-a-standard-topic-or-an-unknown-value-rejected";

    /// <summary>
    /// FifoThroughputScope is refused on a standard topic and for an unknown value; a FIFO topic
    /// accepts MessageGroup but refuses the switch back to Topic, and no refusal changes the value.
    /// </summary>
    [Test]
    [DisplayName("sns.fifo-throughput-scope-on-a-standard-topic-or-an-unknown-value-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsFifo, "SetTopicAttributes accepts FifoThroughputScope on a standard topic instead of refusing it with 'Invalid parameter: AttributeName'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var std = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-ftscope-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = std }));
        var fifo = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-ftscope-") + ".fifo",
            Attributes = new() { ["FifoTopic"] = "true" },
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = fifo }));

        var (code, message) = await Error(() => sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = std, AttributeName = "FifoThroughputScope", AttributeValue = "MessageGroup",
        }));
        Ensure((code, message) == ("InvalidParameter", "Invalid parameter: AttributeName"),
            $"FifoThroughputScope on a standard topic gave '{code}': '{message}'");
        var stdAttrs = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = std })).Attributes ?? [];
        Ensure(!stdAttrs.ContainsKey("FifoThroughputScope"),
            $"the refused write left FifoThroughputScope='{stdAttrs.GetValueOrDefault("FifoThroughputScope")}' on a standard topic");

        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = fifo, AttributeName = "FifoThroughputScope", AttributeValue = "MessageGroup",
        });
        Ensure(await Scope(ctx, fifo) == "MessageGroup", "a FIFO topic must accept FifoThroughputScope=MessageGroup");
        (code, message) = await Error(() => sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = fifo, AttributeName = "FifoThroughputScope", AttributeValue = "Partition",
        }));
        Ensure((code, message) == ("InvalidParameter", "Invalid parameter: Invalid FifoThroughputScope: Partition"),
            $"an unknown FifoThroughputScope value gave '{code}': '{message}'");
        var kept = await Scope(ctx, fifo);
        Ensure(kept == "MessageGroup", $"the refused value changed FifoThroughputScope to '{kept}'");

        (code, message) = await Error(() => sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = fifo, AttributeName = "FifoThroughputScope", AttributeValue = "Topic",
        }));
        Ensure((code, message) == ("InvalidParameter", "Invalid parameter: Modifying FifoThroughputScope from MessageGroup to Topic is not supported"),
            $"switching FifoThroughputScope back from MessageGroup to Topic gave '{code}': '{message}'");
        var final = await Scope(ctx, fifo);
        Ensure(final == "MessageGroup", $"the refused switch back changed FifoThroughputScope to '{final}'");
    }

    private static async Task<string?> Scope(FidelityContext ctx, string arn)
        => (await ctx.Sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes?.GetValueOrDefault("FifoThroughputScope");

    /// <summary>(Code, Message) of the error <c>fn</c> raises; an unexpected success raises, naming it.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }
}
