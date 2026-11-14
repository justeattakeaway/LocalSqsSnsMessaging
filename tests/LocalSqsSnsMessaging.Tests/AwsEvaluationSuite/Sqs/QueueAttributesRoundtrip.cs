// Translated from behavioral/sqs/check_queue_attributes_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.queue-attributes-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueueAttributesRoundtrip
{
    /// <summary>
    /// Create queue with custom attributes, verify via GetQueueAttributes.
    /// </summary>
    [Test]
    [DisplayName("sqs.queue-attributes-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Attributes = new() { ["VisibilityTimeout"] = "45", ["MessageRetentionPeriod"] = "345600" },
            Tags = new() { ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["VisibilityTimeout", "MessageRetentionPeriod"],
        })).Attributes!;
        Ensure(attrs["VisibilityTimeout"] == "45", $"VisibilityTimeout mismatch: '{attrs["VisibilityTimeout"]}' != '45'");
        Ensure(attrs["MessageRetentionPeriod"] == "345600", $"MessageRetentionPeriod mismatch: '{attrs["MessageRetentionPeriod"]}' != '345600'");
    }
}
