// Translated from behavioral/sqs/check_set_queue_attributes_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.set-queue-attributes-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class SetQueueAttributesRoundtrip
{
    /// <summary>
    /// SetQueueAttributes AFTER create is reflected by GetQueueAttributes.
    /// </summary>
    [Test]
    [DisplayName("sqs.set-queue-attributes-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["VisibilityTimeout"] = "30" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var pre = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["VisibilityTimeout"] })).Attributes
            ?? throw new KeyNotFoundException("'Attributes'");
        Ensure(pre.GetValueOrDefault("VisibilityTimeout") == "30",
            $"positive control failed: expected VisibilityTimeout '30' right after create, got '{pre.GetValueOrDefault("VisibilityTimeout")}'");

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["VisibilityTimeout"] = "45" } });
        // SetQueueAttributes changes can take up to 60s to propagate per AWS docs:
        // https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SetQueueAttributes.html
        var deadline = FidelityTime.StartNew();
        Dictionary<string, string> attrs = [];
        while (deadline.Elapsed < TimeSpan.FromSeconds(65.0))
        {
            attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["VisibilityTimeout"] })).Attributes
                ?? throw new KeyNotFoundException("'Attributes'");
            if (attrs.GetValueOrDefault("VisibilityTimeout") == "45")
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        throw Fail($"VisibilityTimeout never reached 45 after SetQueueAttributes (last='{attrs.GetValueOrDefault("VisibilityTimeout")}')");
    }
}
