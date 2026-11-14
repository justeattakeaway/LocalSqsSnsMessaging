// Translated from behavioral/sqs/check_list_dead_letter_source_queues_reflects_redrive_policy.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.list-dead-letter-source-queues-reflects-redrive-policy</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListDeadLetterSourceQueuesReflectsRedrivePolicy
{
    /// <summary>
    /// ListDeadLetterSourceQueues on a DLQ returns exactly the source queues whose RedrivePolicy targets it.
    /// </summary>
    [Test]
    [DisplayName("sqs.list-dead-letter-source-queues-reflects-redrive-policy")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.DeadLetterQueues, "source queue with matching RedrivePolicy missing from set()")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-dlq-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));
        var dlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = dlqUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

        var srcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-src-"),
            Attributes = new() { ["RedrivePolicy"] = RedrivePolicy(dlqArn, 5) },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(srcUrl));

        var unrelatedUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-unrelated-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(unrelatedUrl));

        var otherDlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-otherdlq-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(otherDlqUrl));
        var otherDlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = otherDlqUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        var otherSrcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-othersrc-"),
            Attributes = new() { ["RedrivePolicy"] = RedrivePolicy(otherDlqArn, 5) },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(otherSrcUrl));

        var result = await sqs.ListDeadLetterSourceQueuesAsync(new ListDeadLetterSourceQueuesRequest { QueueUrl = dlqUrl });
        var urls = (result.QueueUrls ?? []).ToHashSet();
        var urlsRepr = urls.Count == 0 ? "set()" : "{" + string.Join(", ", urls.Select(u => $"'{u}'")) + "}";
        Ensure(urls.Contains(srcUrl), $"source queue with matching RedrivePolicy missing from {urlsRepr}");
        Ensure(!urls.Contains(unrelatedUrl), $"queue with no RedrivePolicy at all wrongly listed in {urlsRepr}");
        Ensure(!urls.Contains(otherSrcUrl),
            $"queue whose RedrivePolicy targets a DIFFERENT dlq ({otherDlqArn}) wrongly listed in {urlsRepr}");
    }

    /// <summary><c>json.dumps({"deadLetterTargetArn": arn, "maxReceiveCount": n})</c>, byte for byte.</summary>
    private static string RedrivePolicy(string deadLetterTargetArn, int maxReceiveCount) =>
        $"{{\"deadLetterTargetArn\": \"{deadLetterTargetArn}\", \"maxReceiveCount\": {maxReceiveCount}}}";
}
