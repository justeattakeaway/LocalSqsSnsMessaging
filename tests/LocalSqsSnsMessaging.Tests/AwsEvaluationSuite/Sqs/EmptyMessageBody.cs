// Translated from behavioral/sqs/check_empty_message_body.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.empty-message-body</summary>
[Category("AwsEvaluationSuite")]
public sealed class EmptyMessageBody
{
    private const string ErrMissingParameter = "MissingParameter";

    /// <summary>
    /// SendMessage with an empty body is rejected and nothing reaches the queue, while a normal send
    /// on the same queue is accepted and delivered.
    /// </summary>
    [Test]
    [DisplayName("sqs.empty-message-body")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        // ---- positive control: the queue accepts and delivers a normal send ------------------ #
        const string controlBody = "control-body";
        var sent = await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = controlBody });
        Ensure(!string.IsNullOrEmpty(sent.MessageId), $"control send returned no MessageId: {Repr(sent)}");
        await AwaitVisible(sqs, url, 1);
        var received = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 5 });
        var messages = received.Messages ?? [];
        Ensure(messages.Count > 0, $"control message never became receivable: {Repr(received)}");
        Ensure(messages[0].Body == controlBody,
            $"control message body was {messages[0].Body}, expected {controlBody}");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = messages[0].ReceiptHandle });
        await AwaitVisible(sqs, url, 0);

        // ---- break exactly one thing: an empty body on the same queue ------------------------- #
        var code = await ErrorCode(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "" }));
        Ensure(code == (ErrMissingParameter, "The request must contain the parameter MessageBody."),
            $"expected {ErrMissingParameter}, got {code}");

        // ---- effect absent: an independent read shows nothing landed -------------------------- #
        await AwaitVisible(sqs, url, 0);
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
        Ensure(resp.Messages is null or { Count: 0 },
            $"the rejected empty-body send appears to have landed anyway: {Repr(resp)}");
    }

    /// <summary>Raised when a call real AWS rejects is accepted.</summary>
    private sealed class UnexpectedSuccessException(string message) : Exception(message);

    /// <summary>The exact AWS error (code, message); raises if the call succeeds.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (resp, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            if (string.IsNullOrEmpty(error.Code))
            {
                throw new FidelityAssertionException($"call raised with no AWS error code: {error}", error.Exception);
            }

            return (error.Code, error.Message);
        }

        throw new UnexpectedSuccessException($"the empty-body send was ACCEPTED and returned {Repr(resp)}");
    }

    /// <summary>Poll ApproximateNumberOfMessages until it reads `want`, else fail with the last value.</summary>
    private static async Task<string> AwaitVisible(AmazonSQSClient sqs, string url, int want, double timeout = 60)
    {
        var seen = "";
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < timeout)
        {
            seen = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["ApproximateNumberOfMessages"] }))
                .Attributes!["ApproximateNumberOfMessages"];
            if (seen == want.ToString())
            {
                return seen;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"queue never reported {want} visible messages; last read {seen}");
    }

    private static string Repr(object? value)
    {
        try
        {
            return JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object));
        }
        catch (Exception)
        {
            return value?.ToString() ?? "None";
        }
    }
}
