// Translated from behavioral/events/check_put_targets_fifo_sqs_dead_letter_config_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using Tag = Amazon.EventBridge.Model.Tag;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-targets-fifo-sqs-dead-letter-config-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutTargetsFifoSqsDeadLetterConfigRejected
{
    private const string Check = "put-targets-fifo-sqs-dead-letter-config-rejected";

    /// <summary>
    /// PutTargets refuses a target whose DeadLetterConfig names a FIFO queue, with
    /// ValidationException and its exact message, while the same target with a standard queue as its
    /// dead-letter queue is accepted.
    /// </summary>
    [Test]
    [DisplayName("events.put-targets-fifo-sqs-dead-letter-config-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeDeliveryFailures, "PutTargets accepts a DeadLetterConfig naming a FIFO queue; AWS refuses it with 'SQS FIFO is not supported by Dead Letter Queues.'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;

        var stdTargetUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-tgt-"),
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(stdTargetUrl));
        var stdTargetArn = await QueueArn(sqs, stdTargetUrl);

        var stdDlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-dlq-"),
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(stdDlqUrl));
        var stdDlqArn = await QueueArn(sqs, stdDlqUrl);

        var fifoDlqName = FidelityContext.UniqueName("sim2real-behav-dlq-") + ".fifo";
        var fifoDlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = fifoDlqName,
            Attributes = new() { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "true" },
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(fifoDlqUrl));
        var fifoDlqArn = await QueueArn(sqs, fifoDlqUrl);

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
            Targets =
            [
                new Target
                {
                    Id = "fifo-dlq-target",
                    Arn = stdTargetArn,
                    DeadLetterConfig = new DeadLetterConfig { Arn = fifoDlqArn },
                },
            ],
        }));
        Ensure(
            (code, message) == ("ValidationException", "SQS FIFO is not supported by Dead Letter Queues."),
            $"expected the FIFO dead-letter queue to be refused, got '{code}' '{message}'");

        var afterRejection = await Targets();
        Ensure(afterRejection.Count == 0, $"the rejected PutTargets left a target registered: {Show(afterRejection)}");

        // Positive control: the identical target with a standard-queue dead-letter config is accepted.
        var put = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            EventBusName = busName,
            Targets =
            [
                new Target
                {
                    Id = "std-dlq-target",
                    Arn = stdTargetArn,
                    DeadLetterConfig = new DeadLetterConfig { Arn = stdDlqArn },
                },
            ],
        });
        Ensure(put.FailedEntryCount == 0, $"PutTargets with a standard DLQ failed: FailedEntryCount={put.FailedEntryCount}");

        var registered = await Targets();
        Ensure(
            registered.Count == 1 && registered[0].Id == "std-dlq-target",
            $"expected exactly the standard-DLQ target registered, got {Show(registered)}");
        Ensure(
            registered[0].DeadLetterConfig?.Arn == stdDlqArn,
            $"registered target's DeadLetterConfig does not carry the standard queue's ARN: {Show([registered[0]])}");

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

    private static async Task<string> QueueArn(AmazonSQSClient sqs, string url) =>
        (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];

    private static string Show(List<Target> targets) =>
        "[" + string.Join(", ", targets.Select(t => $"{{'Id': '{t.Id}', 'Arn': '{t.Arn}', 'DeadLetterConfig': {(t.DeadLetterConfig is null ? "None" : $"{{'Arn': '{t.DeadLetterConfig.Arn}'}}")}}}")) + "]";
}
