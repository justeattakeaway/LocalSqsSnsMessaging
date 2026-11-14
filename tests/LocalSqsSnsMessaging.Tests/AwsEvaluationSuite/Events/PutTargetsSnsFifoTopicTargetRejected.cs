// Translated from behavioral/events/check_put_targets_sns_fifo_topic_target_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using Tag = Amazon.EventBridge.Model.Tag;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-targets-sns-fifo-topic-target-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutTargetsSnsFifoTopicTargetRejected
{
    private const string Check = "put-targets-sns-fifo-topic-target-rejected";

    /// <summary>
    /// PutTargets refuses a FIFO SNS topic as a target, with ValidationException and its exact
    /// message, while the identical rule accepts a standard SNS topic.
    /// </summary>
    [Test]
    [DisplayName("events.put-targets-sns-fifo-topic-target-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sns = ctx.Sns;

        var fifoTopicName = FidelityContext.UniqueName("sim2real-behav-topic-") + ".fifo";
        var fifoTopicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = fifoTopicName,
            Attributes = new() { ["FifoTopic"] = "true", ["ContentBasedDeduplication"] = "true" },
            Tags = [new Amazon.SimpleNotificationService.Model.Tag { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = fifoTopicArn }));

        var stdTopicName = FidelityContext.UniqueName("sim2real-behav-topic-");
        var stdTopicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = stdTopicName,
            Tags = [new Amazon.SimpleNotificationService.Model.Tag { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = stdTopicArn }));

        var busName = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName, Tags = [new Tag { Key = "sim2real", Value = Check }] });
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName }));

        var ruleName = FidelityContext.UniqueName("sim2real-behav-rule-");
        await events.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventBusName = busName,
            EventPattern = """{"source": ["sim2real.probe"]}""",
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        });

        ctx.OnCleanup(async () =>
        {
            var ids = (await Targets()).Select(t => t.Id).ToList();
            if (ids.Count > 0)
            {
                await events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, EventBusName = busName, Ids = ids });
            }

            await events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName, EventBusName = busName });
        });

        var (code, message) = await Error(() => events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            EventBusName = busName,
            Targets = [new Target { Id = "fifo-sns-target", Arn = fifoTopicArn }],
        }));
        Ensure(
            (code, message) == ("ValidationException", "SNS FIFO is not a supported service for a target."),
            $"expected the FIFO SNS topic target to be refused, got '{code}' '{message}'");

        var afterRejection = await Targets();
        Ensure(afterRejection.Count == 0, $"the rejected PutTargets left a target registered: {Show(afterRejection)}");

        // Positive control: the identical call against a standard SNS topic is accepted.
        var put = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            EventBusName = busName,
            Targets = [new Target { Id = "std-sns-target", Arn = stdTopicArn }],
        });
        Ensure(put.FailedEntryCount == 0, $"PutTargets with a standard SNS topic failed: FailedEntryCount={put.FailedEntryCount}");

        var registered = await Targets();
        Ensure(
            registered.Count == 1 && registered[0].Id == "std-sns-target",
            $"expected exactly the standard SNS topic target registered, got {Show(registered)}");
        Ensure(registered[0].Arn == stdTopicArn, $"registered target does not carry the standard topic's ARN: {Show([registered[0]])}");

        // Python indexes list_targets_by_rule(...)["Targets"], which raises KeyError when the response omits it.
        async Task<List<Target>> Targets() =>
            (await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = ruleName, EventBusName = busName })).Targets
                ?? throw new KeyNotFoundException("'Targets'");
    }

    /// <summary>
    /// Call fn(); return the AWS error (Code, Message) if it raised a ClientError. An unexpected
    /// SUCCESS raises, because an emulator ACCEPTING input real AWS refuses is the strongest signal
    /// available.
    /// </summary>
    private static async Task<(string Code, string Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code ?? "", error.Message ?? "");
        }

        throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }

    private static string Show(List<Target> targets) => "[" + string.Join(", ", targets.Select(t => $"{{'Id': '{t.Id}', 'Arn': '{t.Arn}'}}")) + "]";
}
