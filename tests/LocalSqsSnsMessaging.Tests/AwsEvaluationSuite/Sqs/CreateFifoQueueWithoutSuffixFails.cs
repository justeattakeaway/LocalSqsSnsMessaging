// Translated from behavioral/sqs/check_create_fifo_queue_without_suffix_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.create-fifo-queue-without-suffix-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateFifoQueueWithoutSuffixFails
{
    /// <summary>
    /// Creating a FifoQueue=true queue whose name lacks the .fifo suffix must fail with the
    /// specific validation error, and the rejected name must never come into existence — while the
    /// identical account and API remain healthy for a properly-suffixed FIFO queue.
    /// </summary>
    [Test]
    [DisplayName("sqs.create-fifo-queue-without-suffix-fails")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Fifo, "CreateQueue with FifoQueue=true and no .fifo suffix succeeds instead of InvalidParameterValue")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;

        var goodName = FidelityContext.UniqueName("sim2real-behav-fifobad-") + ".fifo";
        var goodUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = goodName, Attributes = new() { ["FifoQueue"] = "true" } })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(goodUrl));
        Ensure(goodUrl.EndsWith(".fifo", StringComparison.Ordinal), $"positive control: expected .fifo URL, got '{goodUrl}'");

        var badName = FidelityContext.UniqueName("sim2real-behav-fifobad-");  // deliberately missing ".fifo" suffix
        var exc = await ExpectAwsError(
            () => sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = badName, Attributes = new() { ["FifoQueue"] = "true" } }),
            "InvalidParameterValue");
        const string expectedMessage =
            "The name of a FIFO queue can only include alphanumeric characters, hyphens, or " +
            "underscores, must end with .fifo suffix and be 1 to 80 in length.";
        var message = exc.Message;
        Ensure(message == expectedMessage, $"expected message '{expectedMessage}', got '{message}'");

        // The rejected name must never come into existence.
        exc = await ExpectAwsError(
            () => sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = badName }), "AWS.SimpleQueueService.NonExistentQueue");
        message = exc.Message;
        Ensure(message == "The specified queue does not exist.",
            $"expected message 'The specified queue does not exist.', got '{message}'");
    }

    /// <summary>
    /// Call <c>fn</c>, which AWS must refuse with one of <c>codes</c>; return the <c>ClientError</c>.
    ///
    /// A wrong code fails raised from the original error, so the failure keeps AWS's answer.
    /// </summary>
    private static async Task<AwsError> ExpectAwsError<T>(Func<Task<T>> fn, params string[] codes)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            if (codes.Contains(error.Code))
            {
                return error;
            }

            throw new FidelityAssertionException($"expected {string.Join(" or ", codes)}, got {error.Code}", error.Exception);
        }

        throw Fail($"expected {string.Join(" or ", codes)}, but the call succeeded: {result}");
    }
}
