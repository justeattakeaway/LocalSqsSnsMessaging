// Translated from behavioral/sqs/check_create_queue_after_set_queue_attributes_compares_current_values.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.create-queue-after-set-queue-attributes-compares-current-values</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateQueueAfterSetQueueAttributesComparesCurrentValues
{
    private static readonly Dictionary<string, string> Tags = new() { ["sim2real"] = "create-queue-after-set-queue-attributes-compares-current-values" };
    private const double PropagationDeadlineS = 90.0;

    /// <summary>
    /// CreateQueue on an existing name compares against the CURRENT attributes a later
    /// SetQueueAttributes set, not the creation-time ones: no Attributes, or the current value
    /// resubmitted, returns the same queue; a differing value is refused naming the current value.
    /// </summary>
    [Test]
    [DisplayName("sqs.create-queue-after-set-queue-attributes-compares-current-values")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-cqsqa-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name, Attributes = new() { ["VisibilityTimeout"] = "30" }, Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var createdVt = await ReadVisibilityTimeout(sqs, url);
        Ensure(createdVt == "30", $"positive control: VisibilityTimeout read back as '{createdVt}'");

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["VisibilityTimeout"] = "90" } });
        await PollAttribute(sqs, url, "VisibilityTimeout", "90");

        // (1) CreateQueue with NO Attributes at all is accepted as the same queue, and the attribute
        // SetQueueAttributes changed stays at its current value rather than reverting to the
        // creation-time one or to the service's documented default.
        var noAttrs = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name })).QueueUrl;
        Ensure(noAttrs == url, $"CreateQueue with no Attributes returned '{noAttrs}', expected the existing queue '{url}'");
        var vtAfterNoAttrs = await ReadVisibilityTimeout(sqs, url);
        Ensure(vtAfterNoAttrs == "90",
            $"a no-Attributes CreateQueue changed VisibilityTimeout to '{vtAfterNoAttrs}', " +
            "expected it to stay at the current value '90'");

        // (2) CreateQueue with a value that differs from the CURRENT one (90), not the creation-time
        // one (30), is refused, and the refusal names the current value.
        var (code, message) = await Err(() => sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = new() { ["VisibilityTimeout"] = "45" } }));
        Ensure(code == "QueueAlreadyExists", $"expected QueueAlreadyExists, got '{code}'");
        const string expectedMessage =
            "A queue already exists with the same name and a different value for attribute " +
            "VisibilityTimeout";
        Ensure(message == expectedMessage, $"expected message '{expectedMessage}', got '{message}'");
        var vtAfterConflict = await ReadVisibilityTimeout(sqs, url);
        Ensure(vtAfterConflict == "90", $"the refused CreateQueue changed VisibilityTimeout to '{vtAfterConflict}'");

        // (3) CreateQueue with the CURRENT value (90) re-submitted, which differs from the
        // creation-time value (30), is accepted as the same queue.
        var matched = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = new() { ["VisibilityTimeout"] = "90" } })).QueueUrl;
        Ensure(matched == url, $"CreateQueue with the current VisibilityTimeout returned '{matched}', expected '{url}'");
        var vtFinal = await ReadVisibilityTimeout(sqs, url);
        Ensure(vtFinal == "90", $"VisibilityTimeout read '{vtFinal}' after the matching re-create");
    }

    private static async Task<string> ReadVisibilityTimeout(AmazonSQSClient sqs, string url)
        => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["VisibilityTimeout"] })).Attributes!["VisibilityTimeout"];

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
        var deadline = Stopwatch.StartNew();
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

            await Task.Delay(TimeSpan.FromSeconds(2.0));
        }
    }
}
