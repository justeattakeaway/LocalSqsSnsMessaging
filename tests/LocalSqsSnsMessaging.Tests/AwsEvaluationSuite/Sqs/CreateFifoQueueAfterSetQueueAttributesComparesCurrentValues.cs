// Translated from behavioral/sqs/check_create_fifo_queue_after_set_queue_attributes_compares_current_values.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.create-fifo-queue-after-set-queue-attributes-compares-current-values</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateFifoQueueAfterSetQueueAttributesComparesCurrentValues
{
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real"] = "create-fifo-queue-after-set-queue-attributes-compares-current-values" };
    private const double PropagationDeadlineS = 90.0;

    /// <summary>
    /// CreateQueue on an existing FIFO queue's name compares ContentBasedDeduplication against
    /// the CURRENT value a later SetQueueAttributes set, not the creation-time one: a differing
    /// value is refused naming the current value; the current value resubmitted returns it.
    /// </summary>
    [Test]
    [DisplayName("sqs.create-fifo-queue-after-set-queue-attributes-compares-current-values")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-cqsqafifo-") + ".fifo";
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "false" },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var createdCbd = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url, AttributeNames = ["ContentBasedDeduplication"],
        })).Attributes!["ContentBasedDeduplication"];
        Ensure(createdCbd == "false", $"positive control: ContentBasedDeduplication read back as '{createdCbd}'");

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["ContentBasedDeduplication"] = "true" } });
        await PollAttribute(sqs, url, "ContentBasedDeduplication", "true");

        // CreateQueue with a value that differs from the CURRENT one (true), not the creation-time
        // one (false), is refused, and the refusal names the current value.
        var (code, message) = await Err(() => sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "false" },
        }));
        Ensure(code == "QueueAlreadyExists", $"expected QueueAlreadyExists, got '{code}'");
        const string expectedMessage =
            "A queue already exists with the same name and a different value for attribute " +
            "ContentBasedDeduplication";
        Ensure(message == expectedMessage, $"expected message '{expectedMessage}', got '{message}'");
        var cbdAfterConflict = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url, AttributeNames = ["ContentBasedDeduplication"],
        })).Attributes!["ContentBasedDeduplication"];
        Ensure(cbdAfterConflict == "true", $"the refused CreateQueue changed ContentBasedDeduplication to '{cbdAfterConflict}'");

        // CreateQueue with the CURRENT value (true) re-submitted, which differs from the
        // creation-time value (false), is accepted as the same queue.
        var matched = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "true" },
        })).QueueUrl;
        Ensure(matched == url, $"CreateQueue with the current ContentBasedDeduplication returned '{matched}', expected '{url}'");
        var cbdFinal = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url, AttributeNames = ["ContentBasedDeduplication"],
        })).Attributes!["ContentBasedDeduplication"];
        Ensure(cbdFinal == "true", $"ContentBasedDeduplication read '{cbdFinal}' after the matching re-create");
    }

    /// <summary>(Code, Message) of the ClientError <c>fn</c> raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Err<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }

    /// <summary>
    /// Poll GetQueueAttributes until <c>attribute</c> reads <c>want</c>; SetQueueAttributes can take up
    /// to 60 s to propagate. Raises at the deadline.
    /// </summary>
    private static async Task<string> PollAttribute(AmazonSQSClient sqs, string url, string attribute, string want)
    {
        var deadline = FidelityTime.StartNew();
        while (true)
        {
            var got = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = [attribute] })).Attributes![attribute];
            if (got == want)
            {
                return got;
            }

            if (deadline.Elapsed > TimeSpan.FromSeconds(PropagationDeadlineS))
            {
                throw Fail(
                    $"SetQueueAttributes did not take effect within {PropagationDeadlineS}s: " +
                    $"{attribute} still reads '{got}', expected '{want}'");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }
    }
}
