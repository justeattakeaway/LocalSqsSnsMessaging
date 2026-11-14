// Translated from behavioral/sqs/check_redrive_policy_max_receive_count_out_of_range_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.redrive-policy-max-receive-count-out-of-range-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class RedrivePolicyMaxReceiveCountOutOfRangeRejected
{
    private static Dictionary<string, string> Tags() => new() { ["sim2real"] = "redrive-policy-max-receive-count-out-of-range-rejected" };

    /// <summary>
    /// A RedrivePolicy with maxReceiveCount 0 or 1001 is refused with InvalidParameterValue and the
    /// exact range message, storing nothing; the boundary 1000 is accepted, read back, and links the
    /// source to the DLQ.
    /// </summary>
    [Test]
    [DisplayName("sqs.redrive-policy-max-receive-count-out-of-range-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var baseName = FidelityContext.UniqueName("sim2real-behav-mrc-");
        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = baseName + "-dlq", Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));
        var dlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = dlqUrl,
            AttributeNames = ["QueueArn"],
        })).Attributes!["QueueArn"];
        var srcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = baseName + "-src", Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(srcUrl));

        foreach (var bad in new[] { 0, 1001 })
        {
            var doc = Dumps(dlqArn, bad);
            var (code, msg) = await Err(() => sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
            {
                QueueUrl = srcUrl,
                Attributes = new() { ["RedrivePolicy"] = doc },
            }));
            Ensure(code == "InvalidParameterValue", $"maxReceiveCount={bad} gave code '{code}'");
            var expected = $"Value {doc} for parameter RedrivePolicy is invalid. Reason: Invalid value "
                           + $"for maxReceiveCount: {bad}, valid values are from 1 to 1000 both inclusive.";
            Ensure(msg == expected, $"maxReceiveCount={bad} gave message '{msg}'");
            var stored = await Redrive(sqs, srcUrl);
            Ensure(stored is null,
                $"the refused maxReceiveCount={bad} policy was stored anyway: '{stored}'");
        }

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = srcUrl,
            Attributes = new() { ["RedrivePolicy"] = Dumps(dlqArn, 1000) },
        });
        var stored2 = await Redrive(sqs, srcUrl);
        Ensure(stored2 is not null, "the in-range maxReceiveCount=1000 policy was not stored");
        var parsed = JsonNode.Parse(stored2)!;
        Ensure(parsed["maxReceiveCount"] is JsonValue n && n.GetValueKind() == JsonValueKind.Number && n.GetValue<double>() == 1000,
            $"the stored policy reads maxReceiveCount={parsed["maxReceiveCount"]?.ToJsonString() ?? "None"}, expected 1000");
        Ensure(Str(parsed["deadLetterTargetArn"]) == dlqArn,
            "the stored policy does not name the DLQ it was given");
        // ListDeadLetterSourceQueues is eventually consistent; it can lag the policy by ~40 s.
        var deadline = Stopwatch.StartNew();
        List<string> sources;
        while (true)
        {
            sources = (await sqs.ListDeadLetterSourceQueuesAsync(new ListDeadLetterSourceQueuesRequest { QueueUrl = dlqUrl })).QueueUrls ?? [];
            if (sources.Count > 0 || deadline.Elapsed > TimeSpan.FromSeconds(90))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        Ensure(sources.SequenceEqual([srcUrl]),
            $"after the accepted policy the DLQ lists {sources.Count} source queues, expected exactly "
            + "the one source");
    }

    /// <summary>(Code, Message) of the ClientError <paramref name="fn"/> raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Err<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task<string?> Redrive(IAmazonSQS sqs, string url) =>
        (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["RedrivePolicy"] }))
        .Attributes?.GetValueOrDefault("RedrivePolicy");

    /// <summary><c>json.dumps({"deadLetterTargetArn": arn, "maxReceiveCount": count})</c>, byte for byte.</summary>
    private static string Dumps(string arn, int count) =>
        $"{{\"deadLetterTargetArn\": {JsonValue.Create(arn).ToJsonString()}, \"maxReceiveCount\": {count}}}";

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
}
