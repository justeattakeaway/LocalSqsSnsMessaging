// Translated from behavioral/sqs/check_set_queue_attributes_readonly_attribute_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.set-queue-attributes-readonly-attribute-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SetQueueAttributesReadonlyAttributeRejected
{
    private const string Check = "set-queue-attributes-readonly-attribute-rejected";

    /// <summary>
    /// SetQueueAttributes rejects writes to a read-only attribute (ApproximateNumberOfMessages)
    /// instead of silently accepting/ignoring it, while a real mutable attribute still persists.
    /// </summary>
    [Test]
    [DisplayName("sqs.set-queue-attributes-readonly-attribute-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Validation, "expected a write to the read-only ApproximateNumberOfMessages to be rejected, but SetQueueAttributes succeeded")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-roattr-"),
            Tags = new() { ["sim2real-check"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var before = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["ApproximateNumberOfMessages"],
        })).Attributes!["ApproximateNumberOfMessages"];

        var (code, message) = await Error(() => sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new() { ["ApproximateNumberOfMessages"] = "1234" },
        }));
        Ensure(code == "InvalidAttributeName",
            "expected InvalidAttributeName rejecting a write to the read-only " +
            $"ApproximateNumberOfMessages attribute, got '{code}'");
        Ensure(message == "Unknown Attribute ApproximateNumberOfMessages.", $"unexpected refusal message '{message}'");

        // effect absent: the read-only attribute did not take on the rejected value
        var after = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["ApproximateNumberOfMessages"],
        })).Attributes!["ApproximateNumberOfMessages"];
        Ensure(after != "1234", $"rejected write still took effect: ApproximateNumberOfMessages='{after}'");
        Ensure(after == before, $"expected ApproximateNumberOfMessages to stay '{before}', got '{after}'");

        // positive control: a genuinely mutable attribute is accepted and persisted
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["VisibilityTimeout"] = "45" } });
        var control = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["VisibilityTimeout"],
        })).Attributes!["VisibilityTimeout"];
        Ensure(control == "45", $"expected VisibilityTimeout='45' after a genuinely mutable set, got '{control}'");
    }

    private static async Task<(string? Code, string? Message)> Error(Func<Task<SetQueueAttributesResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {{'HTTPStatusCode': {(int)result!.HttpStatusCode}}}");
    }
}
