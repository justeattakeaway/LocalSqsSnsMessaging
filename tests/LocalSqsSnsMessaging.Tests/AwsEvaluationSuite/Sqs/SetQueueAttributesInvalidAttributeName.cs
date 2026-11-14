// Translated from behavioral/sqs/check_set_queue_attributes_invalid_attribute_name.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.set-queue-attributes-invalid-attribute-name</summary>
[Category("AwsEvaluationSuite")]
public sealed class SetQueueAttributesInvalidAttributeName
{
    /// <summary>
    /// SetQueueAttributes rejects an attribute key that SQS does not recognize, while a
    /// genuinely mutable attribute on the same queue is still accepted and persisted.
    /// </summary>
    [Test]
    [DisplayName("sqs.set-queue-attributes-invalid-attribute-name")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var (code, message) = await Err(() => sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new() { ["NotARealAttribute"] = "x" },
        }));
        Ensure(code == "InvalidAttributeName", $"expected InvalidAttributeName, got {code}");
        Ensure(message == "Unknown Attribute NotARealAttribute.", $"unexpected message '{message}'");

        // positive control: a genuinely mutable attribute on the same queue is accepted and
        // persisted, so the rejection above is specific to the bogus key, not a broken queue
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["VisibilityTimeout"] = "45" } });
        var control = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["VisibilityTimeout"],
        })).Attributes!["VisibilityTimeout"];
        Ensure(control == "45", $"expected VisibilityTimeout to persist as '45', got '{control}'");
    }

    private static async Task<(string? Code, string? Message)> Err(Func<Task<SetQueueAttributesResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected SetQueueAttributes to reject NotARealAttribute, but it succeeded: {{'HTTPStatusCode': {(int)result!.HttpStatusCode}}}");
    }
}
