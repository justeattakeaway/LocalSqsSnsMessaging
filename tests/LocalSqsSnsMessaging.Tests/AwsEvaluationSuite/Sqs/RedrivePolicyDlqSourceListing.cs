// Translated from behavioral/sqs/check_redrive_policy_dlq_source_listing.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.redrive-policy-dlq-source-listing</summary>
[Category("AwsEvaluationSuite")]
public sealed class RedrivePolicyDlqSourceListing
{
    /// <summary>
    /// A source queue with a RedrivePolicy pointing at a DLQ shows up in ListDeadLetterSourceQueues,
    /// an unrelated queue does not, and the source's own RedrivePolicy round-trips exactly.
    /// </summary>
    [Test]
    [DisplayName("sqs.redrive-policy-dlq-source-listing")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));
        var dlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = dlqUrl,
            AttributeNames = ["QueueArn"],
        })).Attributes!["QueueArn"];

        // json.dumps({"deadLetterTargetArn": dlq_arn, "maxReceiveCount": 3})
        var redrivePolicy = $"{{\"deadLetterTargetArn\": {JsonValue.Create(dlqArn).ToJsonString()}, \"maxReceiveCount\": 3}}";
        var srcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["RedrivePolicy"] = redrivePolicy },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(srcUrl));

        var unrelatedUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(unrelatedUrl));

        const int timeoutS = 180;
        var result = await Poll(
            async () => (await sqs.ListDeadLetterSourceQueuesAsync(new ListDeadLetterSourceQueuesRequest { QueueUrl = dlqUrl })).QueueUrls ?? [],
            urls => urls.Contains(srcUrl),
            timeoutS: timeoutS,
            intervalS: 5);
        Ensure(result.Contains(srcUrl), $"source queue not listed in DLQ source queues: [{string.Join(", ", result)}]");
        Ensure(!result.Contains(unrelatedUrl),
            $"queue with no RedrivePolicy targeting this DLQ was listed as a source: [{string.Join(", ", result)}]");

        var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = srcUrl,
            AttributeNames = ["RedrivePolicy"],
        })).Attributes!;
        var roundTripped = JsonNode.Parse(attrs["RedrivePolicy"])!;
        Ensure(Str(roundTripped["deadLetterTargetArn"]) == dlqArn,
            $"RedrivePolicy did not round-trip the DLQ arn: {roundTripped.ToJsonString()}");
        Ensure(roundTripped["maxReceiveCount"] is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.GetValue<double>() == 3,
            $"RedrivePolicy did not round-trip maxReceiveCount: {roundTripped.ToJsonString()}");
    }

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS)
    {
        var deadline = Stopwatch.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                return val;
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalS));
            val = await fn();
        }

        return val;
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
}
