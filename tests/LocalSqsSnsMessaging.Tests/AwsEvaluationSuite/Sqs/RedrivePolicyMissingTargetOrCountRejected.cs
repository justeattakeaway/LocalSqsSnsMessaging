// Translated from behavioral/sqs/check_redrive_policy_missing_target_or_count_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.redrive-policy-missing-target-or-count-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class RedrivePolicyMissingTargetOrCountRejected
{
    private static Dictionary<string, string> Tags() => new() { ["sim2real"] = "redrive-policy-missing-target-or-count-rejected" };

    /// <summary>
    /// A RedrivePolicy naming a nonexistent DLQ, or omitting maxReceiveCount, is refused with
    /// InvalidParameterValue and its exact message and stores nothing; the same policy naming the
    /// real DLQ with a count is accepted.
    /// </summary>
    [Test]
    [DisplayName("sqs.redrive-policy-missing-target-or-count-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var baseName = FidelityContext.UniqueName("sim2real-behav-rpv-");
        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = baseName + "-dlq", Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));
        var dlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = dlqUrl,
            AttributeNames = ["QueueArn"],
        })).Attributes!["QueueArn"];
        var srcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = baseName + "-src", Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(srcUrl));

        var ghostArn = dlqArn[..dlqArn.LastIndexOf(':')] + ":" + baseName + "-never-created";
        // json.dumps({"deadLetterTargetArn": ghost_arn, "maxReceiveCount": 3})
        var ghost = $"{{\"deadLetterTargetArn\": {Quote(ghostArn)}, \"maxReceiveCount\": 3}}";
        var (code, msg) = await Err(() => sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = srcUrl,
            Attributes = new() { ["RedrivePolicy"] = ghost },
        }));
        Ensure(code == "InvalidParameterValue", $"a ghost DLQ target gave code '{code}'");
        Ensure(msg == $"Value {ghost} for parameter RedrivePolicy is invalid. Reason: Dead letter "
                      + "target does not exist.", $"a ghost DLQ target gave message '{msg}'");
        Ensure(await Redrive(sqs, srcUrl) is null, "the policy naming a ghost DLQ was stored anyway");

        // json.dumps({"deadLetterTargetArn": dlq_arn})
        var noCount = $"{{\"deadLetterTargetArn\": {Quote(dlqArn)}}}";
        (code, msg) = await Err(() => sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = srcUrl,
            Attributes = new() { ["RedrivePolicy"] = noCount },
        }));
        Ensure(code == "InvalidParameterValue", $"a policy without maxReceiveCount gave code '{code}'");
        Ensure(msg == $"Value {noCount} for parameter RedrivePolicy is invalid. Reason: Redrive "
                      + "policy does not contain mandatory attribute: maxReceiveCount.",
            $"a policy without maxReceiveCount gave message '{msg}'");
        Ensure(await Redrive(sqs, srcUrl) is null, "the policy without maxReceiveCount was stored anyway");

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = srcUrl,
            Attributes = new() { ["RedrivePolicy"] = $"{{\"deadLetterTargetArn\": {Quote(dlqArn)}, \"maxReceiveCount\": 3}}" },
        });
        var stored = await Redrive(sqs, srcUrl);
        Ensure(stored is not null, "positive control: the valid redrive policy was not stored");
        var parsed = JsonNode.Parse(stored)!;
        var count = parsed["maxReceiveCount"];
        Ensure(Str(parsed["deadLetterTargetArn"]) == dlqArn
               && count is JsonValue n && n.GetValueKind() == JsonValueKind.Number && n.GetValue<double>() == 3,
            "positive control: the stored policy reads maxReceiveCount="
            + $"{count?.ToJsonString() ?? "None"} and a target that "
            + $"{(Str(parsed["deadLetterTargetArn"]) == dlqArn ? "matches" : "does not match")} the DLQ");
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

    private static string Quote(string s) => JsonValue.Create(s).ToJsonString();

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
}
