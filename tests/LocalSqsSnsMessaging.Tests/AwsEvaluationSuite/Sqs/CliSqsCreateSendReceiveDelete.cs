// Translated from behavioral/sqs/check_cli_sqs_create_send_receive_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.cli-sqs-create-send-receive-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class CliSqsCreateSendReceiveDelete
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// Full SQS lifecycle via pure CLI: create-queue, send, receive, delete — then prove
    /// the deleted queue is gone and unusable via an independent SDK read.
    /// </summary>
    [Test]
    [DisplayName("sqs.cli-sqs-create-send-receive-delete")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var name = XName("q");
        var acct = ctx.Account;
        var sqs = ctx.Sqs;
        var queueUrl = $"{sqs.Config.ServiceURL.TrimEnd('/')}/{acct}/{name}";
        ctx.OnCleanup(() => DeleteQueueIfExists(sqs, queueUrl));

        // The bash script, with each `aws sqs ...` invocation replaced by the SDK call it makes.
        var scriptQueueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = XsvcResourceTags() })).QueueUrl;
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = scriptQueueUrl, MessageBody = "{\"source\":\"cli\"}" });
        // `--query 'Messages[0].Body' --output text` prints "None" when no message came back.
        var received = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = scriptQueueUrl });
        var body = received.Messages is { Count: > 0 } messages ? messages[0].Body : "None";
        await sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = scriptQueueUrl });

        Ensure(body == "{\"source\":\"cli\"}", $"Expected round-tripped body '{{\"source\":\"cli\"}}', got: '{body}'");

        // The script's own delete-queue above is unasserted otherwise: prove the queue
        // is gone via an independent read, and that a mutation against the dead id also fails.
        var e = await FidelityContext.CaptureError(() => sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] }));
        if (e is null)
        {
            throw Fail($"Expected {queueUrl} to be gone after delete-queue");
        }

        Ensure(e.Code == "AWS.SimpleQueueService.NonExistentQueue", $"Expected NonExistentQueue reading deleted queue, got: {e.Code}");
        Ensure(e.Message == "The specified queue does not exist.",
            $"Expected 'The specified queue does not exist.' reading deleted queue, got: '{e.Message}'");

        e = await FidelityContext.CaptureError(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = queueUrl, MessageBody = "post-delete" }));
        if (e is null)
        {
            throw Fail($"Expected send-message on deleted queue {queueUrl} to fail");
        }

        Ensure(e.Code == "AWS.SimpleQueueService.NonExistentQueue", $"Expected NonExistentQueue sending to deleted queue, got: {e.Code}");
        Ensure(e.Message == "The specified queue does not exist.",
            $"Expected 'The specified queue does not exist.' sending to deleted queue, got: '{e.Message}'");
    }

    private static Dictionary<string, string> XsvcResourceTags() => new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    private static async Task DeleteQueueIfExists(AmazonSQSClient sqs, string queueUrl)
    {
        try
        {
            await sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = queueUrl });
        }
        catch (AmazonSQSException exc) when (exc.ErrorCode is "AWS.SimpleQueueService.NonExistentQueue" or "QueueDoesNotExist")
        {
        }
    }
}
