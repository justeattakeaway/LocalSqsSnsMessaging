// Translated from behavioral/sqs/check_immutable_queue_arn_mutation_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.immutable-queue-arn-mutation-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ImmutableQueueArnMutationRejected
{
    private const string ReadOnlyAttributeMessage = "Unknown Attribute QueueArn.";

    /// <summary>
    /// SetQueueAttributes rejects setting the read-only QueueArn with InvalidAttributeName and its exact
    /// message and leaves the ARN unchanged, while a writable attribute set on the same queue succeeds.
    /// </summary>
    [Test]
    [DisplayName("sqs.immutable-queue-arn-mutation-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Validation, "expected rejection, but SetQueueAttributes(QueueArn=...) SUCCEEDED")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-immarn-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var originalArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

        var error = await ClientError(() => sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new() { ["QueueArn"] = "arn:aws:sqs:us-east-1:123456789012:not-a-real-queue" },
        }));
        Ensure(error.Code == "InvalidAttributeName",
            $"expected InvalidAttributeName, got '{error.Code}'");
        Ensure(error.Message == ReadOnlyAttributeMessage,
            $"expected message '{ReadOnlyAttributeMessage}', got '{error.Message}'");

        // Effect absent: the ARN did not change.
        var unchangedArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        Ensure(unchangedArn == originalArn, "QueueArn mutated despite rejected SetQueueAttributes");

        // Positive control: a genuinely writable attribute succeeds on the same queue.
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["VisibilityTimeout"] = "45" } });
        var vt = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["VisibilityTimeout"] })).Attributes!["VisibilityTimeout"];
        Ensure(vt == "45", $"expected VisibilityTimeout '45', got '{vt}'");
    }

    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected rejection, but the call SUCCEEDED and returned {result}");
    }
}
