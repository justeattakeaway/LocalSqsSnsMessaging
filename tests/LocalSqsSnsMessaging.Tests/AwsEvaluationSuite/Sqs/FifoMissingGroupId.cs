// Translated from behavioral/sqs/check_fifo_missing_group_id.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-missing-group-id</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoMissingGroupId
{
    private const string Check = "fifo-missing-group-id";
    private const string ErrMissingParameter = "MissingParameter";

    /// <summary>
    /// SendMessage to a FIFO queue without MessageGroupId is rejected; with it, it lands.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-missing-group-id")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "true" },
            Tags = new() { ["sim2real"] = "true", ["check"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        const string controlBody = "control-has-group-id";
        var control = await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = controlBody, MessageGroupId = "control-group" });
        Ensure(!string.IsNullOrEmpty(control.MessageId),
            $"positive control SendMessage WITH MessageGroupId must succeed, got MessageId={control.MessageId} " +
            "(otherwise a target that rejects every FIFO SendMessage would also pass this check)");

        var error = await FidelityContext.CaptureError(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "no-group" }));
        var code = error is null ? ((string?, string?)?)null : (error.Code, error.Message);
        Ensure(code == (ErrMissingParameter, "The request must contain the parameter MessageGroupId."),
            $"expected {ErrMissingParameter}, got {code?.ToString() ?? "None"} " +
            "(FIFO send without MessageGroupId must be rejected)");

        List<Message> received = [];
        for (var i = 0; i < 6; i++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
            received.AddRange(resp.Messages ?? []);
            if (received.Count > 0)
            {
                break;
            }
        }

        var bodies = received.Select(m => m.Body).ToList();
        Ensure(bodies.SequenceEqual([controlBody]),
            $"expected only the positive control message '{controlBody}' to have landed in the " +
            $"queue, got [{string.Join(", ", bodies)}] (the rejected SendMessage without MessageGroupId must never " +
            "be enqueued, checked via an independent ReceiveMessage)");
    }
}
