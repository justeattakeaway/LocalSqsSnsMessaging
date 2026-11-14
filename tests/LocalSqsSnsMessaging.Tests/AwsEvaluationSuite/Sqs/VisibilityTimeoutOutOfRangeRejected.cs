// Translated from behavioral/sqs/check_visibility_timeout_out_of_range_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.visibility-timeout-out-of-range-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class VisibilityTimeoutOutOfRangeRejected
{
    /// <summary>
    /// SetQueueAttributes rejects a VisibilityTimeout above the 43200s maximum; a valid value still applies.
    /// </summary>
    [Test]
    [DisplayName("sqs.visibility-timeout-out-of-range-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
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

        var (code, message) = await Error(() => sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new() { ["VisibilityTimeout"] = "43201" },
        }));
        Ensure(code == "InvalidAttributeValue", $"expected InvalidAttributeValue, got '{code}'");
        const string expectedMessage = "Invalid value for the parameter VisibilityTimeout.";
        Ensure(message == expectedMessage, $"expected message '{expectedMessage}', got '{message}'");

        // Effect absent: the attribute must remain at its original value.
        var unchanged = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["VisibilityTimeout"] })).Attributes!;
        Ensure(unchanged["VisibilityTimeout"] == "30",
            "a rejected SetQueueAttributes must not change VisibilityTimeout, " +
            $"but it is now '{unchanged["VisibilityTimeout"]}'");

        // Positive control: a value within range is accepted and eventually observable (propagation
        // can take up to 60s per AWS docs) -- poll for it rather than asserting immediately.
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["VisibilityTimeout"] = "45" } });
        await PollUntil(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["VisibilityTimeout"] })).Attributes!["VisibilityTimeout"],
            v => v == "45",
            timeoutSeconds: 65.0,
            intervalSeconds: 3.0);
    }

    private static async Task<(string? Code, string? Message)> Error(Func<Task<SetQueueAttributesResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected SetQueueAttributes to reject VisibilityTimeout=43201, but it succeeded: {{'HTTPStatusCode': {(int)result!.HttpStatusCode}}}");
    }

    private static async Task<string?> PollUntil(Func<Task<string>> probe, Func<string, bool> predicate, double timeoutSeconds = 30.0, double intervalSeconds = 1.0)
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
