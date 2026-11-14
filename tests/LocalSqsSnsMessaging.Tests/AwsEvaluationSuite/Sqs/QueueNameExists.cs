// Translated from behavioral/sqs/check_queue_name_exists.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.queue-name-exists</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueueNameExists
{
    private const string ErrQueueNameExists = "QueueAlreadyExists";

    /// <summary>
    /// create_queue re-using name with different attrs → QueueAlreadyExists, without
    /// mutating the existing queue; re-issuing create_queue with the SAME name and
    /// IDENTICAL attributes succeeds idempotently.
    /// </summary>
    [Test]
    [DisplayName("sqs.queue-name-exists")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["VisibilityTimeout"] = "30" },
            Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "queue-name-exists" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var error = await FidelityContext.CaptureError(() => sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["VisibilityTimeout"] = "60" },
        }));
        var code = error is null ? ((string?, string?)?)null : (error.Code, error.Message);
        Ensure(code == (ErrQueueNameExists,
                "A queue already exists with the same name and a different value for attribute VisibilityTimeout"),
            $"expected {ErrQueueNameExists}, got {(code is null ? "None" : code.ToString())} "
            + "(None means conflicting create silently succeeded)");

        // effect absent: the rejected conflicting create must not have mutated the queue
        var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["VisibilityTimeout"],
        })).Attributes!;
        Ensure(attrs["VisibilityTimeout"] == "30",
            $"VisibilityTimeout changed to '{attrs["VisibilityTimeout"]}' by a rejected create_queue call");

        // restore beat: identical attributes → idempotent success, same URL returned
        var idempotent = await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["VisibilityTimeout"] = "30" },
        });
        Ensure(idempotent.QueueUrl == url,
            $"identical create_queue should return the same URL, got '{idempotent.QueueUrl}' vs '{url}'");
    }
}
