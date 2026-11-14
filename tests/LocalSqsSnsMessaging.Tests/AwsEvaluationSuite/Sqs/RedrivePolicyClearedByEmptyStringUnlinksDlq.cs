// Translated from behavioral/sqs/check_redrive_policy_cleared_by_empty_string_unlinks_dlq.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.redrive-policy-cleared-by-empty-string-unlinks-dlq</summary>
[Category("AwsEvaluationSuite")]
public sealed class RedrivePolicyClearedByEmptyStringUnlinksDlq
{
    private static Dictionary<string, string> Tags() => new() { ["sim2real"] = "redrive-policy-cleared-by-empty-string-unlinks-dlq" };

    /// <summary>
    /// Setting RedrivePolicy to "" removes it: the attribute disappears and the DLQ stops listing
    /// that source in ListDeadLetterSourceQueues, while a sibling source stays listed and re-setting
    /// the policy links the first source again.
    /// </summary>
    [Test]
    [DisplayName("sqs.redrive-policy-cleared-by-empty-string-unlinks-dlq")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.DeadLetterQueues, "ListDeadLetterSourceQueues never lists the linked sources, so the 90s poll outlasts the 60s budget")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var baseName = FidelityContext.UniqueName("sim2real-behav-rpc-");
        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = baseName + "-dlq", Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));
        var dlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = dlqUrl,
            AttributeNames = ["QueueArn"],
        })).Attributes!["QueueArn"];
        var policy = $"{{\"deadLetterTargetArn\": {JsonValue.Create(dlqArn).ToJsonString()}, \"maxReceiveCount\": 5}}";
        var srcA = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = baseName + "-a", Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(srcA));
        var srcB = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = baseName + "-b", Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(srcB));
        foreach (var src in new[] { srcA, srcB })
        {
            await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = src, Attributes = new() { ["RedrivePolicy"] = policy } });
        }

        List<string> SortedPair() => new[] { srcA, srcB }.Order(StringComparer.Ordinal).ToList();

        var both = await Poll(async () => (await Sources(sqs, dlqUrl)).Order(StringComparer.Ordinal).ToList(), v => v.SequenceEqual(SortedPair()));
        Ensure(both.SequenceEqual(SortedPair()),
            $"with both sources linked the DLQ lists {both.Count} sources, expected 2");

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = srcA, Attributes = new() { ["RedrivePolicy"] = "" } });
        var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = srcA,
            AttributeNames = ["RedrivePolicy"],
        })).Attributes ?? [];
        Ensure(!attrs.ContainsKey("RedrivePolicy"),
            $"after clearing with '' the source still reports RedrivePolicy='{attrs.GetValueOrDefault("RedrivePolicy")}'");
        var kept = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = srcB,
            AttributeNames = ["RedrivePolicy"],
        })).Attributes?.GetValueOrDefault("RedrivePolicy");
        Ensure(kept is not null && Str(JsonNode.Parse(kept)!["deadLetterTargetArn"]) == dlqArn,
            "clearing one source's policy disturbed the sibling source's policy");

        var onlyB = await Poll(() => Sources(sqs, dlqUrl), v => v.SequenceEqual([srcB]));
        Ensure(onlyB.SequenceEqual([srcB]),
            $"after unlinking one source the DLQ lists {onlyB.Count} sources "
            + $"({(onlyB.Contains(srcA) ? "including" : "excluding")} the cleared one), expected only the "
            + "sibling");

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = srcA, Attributes = new() { ["RedrivePolicy"] = policy } });
        var again = await Poll(async () => (await Sources(sqs, dlqUrl)).Order(StringComparer.Ordinal).ToList(), v => v.SequenceEqual(SortedPair()));
        Ensure(again.SequenceEqual(SortedPair()),
            $"re-setting the policy did not relink the source: the DLQ lists {again.Count} sources");
    }

    private static async Task<List<string>> Sources(IAmazonSQS sqs, string dlqUrl) =>
        (await sqs.ListDeadLetterSourceQueuesAsync(new ListDeadLetterSourceQueuesRequest { QueueUrl = dlqUrl })).QueueUrls ?? [];

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS = 90)
    {
        var deadline = Stopwatch.StartNew();
        var val = await fn();
        while (!pred(val) && deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            val = await fn();
        }

        return val;
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
}
