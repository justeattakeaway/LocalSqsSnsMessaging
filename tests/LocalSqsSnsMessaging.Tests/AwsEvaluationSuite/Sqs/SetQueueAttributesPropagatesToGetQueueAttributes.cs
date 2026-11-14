// Translated from behavioral/sqs/check_set_queue_attributes_propagates_to_get_queue_attributes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.set-queue-attributes-propagates-to-get-queue-attributes</summary>
[Category("AwsEvaluationSuite")]
public sealed class SetQueueAttributesPropagatesToGetQueueAttributes
{
    private static Dictionary<string, string> Tags() => new() { ["sim2real:managed"] = "true", ["sim2real"] = "set-queue-attributes-propagates" };

    /// <summary>
    /// SetQueueAttributes changes are observable via GetQueueAttributes; propagation is
    /// documented as eventually consistent (up to ~60s) so we poll rather than assert
    /// instantly.
    /// </summary>
    [Test]
    [DisplayName("sqs.set-queue-attributes-propagates-to-get-queue-attributes")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-sqs-setattr-"),
            Attributes = new() { ["VisibilityTimeout"] = "30" },
            Tags = Tags(),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["VisibilityTimeout"] })).Attributes!;
        Ensure(attrs["VisibilityTimeout"] == "30", $"expected initial VisibilityTimeout '30', got '{attrs["VisibilityTimeout"]}'");

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["VisibilityTimeout"] = "45" } });

        // https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SetQueueAttributes.html
        // "the change can take up to 60 seconds for most attributes to propagate"
        await PollUntil(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["VisibilityTimeout"] })).Attributes!["VisibilityTimeout"],
            v => v == "45",
            timeoutSeconds: 65.0);
    }

    private static async Task<string?> PollUntil(Func<Task<string>> probe, Func<string, bool> predicate, double timeoutSeconds = 30.0, double intervalSeconds = 2.0)
    {
        var deadline = Stopwatch.StartNew();
        string? last = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"condition not met within {timeoutSeconds}s (last='{last}')");
    }
}
