// Translated from behavioral/sqs/check_sdk_create_cli_read_sqs.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.sdk-create-cli-read-sqs</summary>
[Category("AwsEvaluationSuite")]
public sealed class SdkCreateCliReadSqs
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// Create queue + send message via SDK, receive via CLI.
    /// </summary>
    [Test]
    [DisplayName("sqs.sdk-create-cli-read-sqs")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var queue = await CreateQueue(ctx);
        var sqs = ctx.Sqs;
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = queue.Url, MessageBody = "sdk-created" });

        // `aws sqs receive-message --queue-url <url>` is a ReceiveMessage with only QueueUrl set; a CLI
        // error (non-zero exit) raises, as the SDK call does. The CLI's `--query "Messages[0].Body"
        // --output text` formatting is dropped: it prints the first message's body, or "None" when
        // the response holds no messages, which is what is compared below.
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queue.Url });
        var stdout = resp.Messages is { Count: > 0 } messages ? messages[0].Body : "None";
        Ensure(stdout.Trim() == "sdk-created", $"Expected 'sdk-created', got '{stdout.Trim()}'");
    }

    private static Dictionary<string, string> XsvcResourceTags() => new()
    {
        [ManagedTagKey] = "true",
        ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-'),
    };

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>Create an SQS queue (registers delete_queue teardown). Returns {name,url,arn}.</summary>
    private static async Task<(string Name, string Url, string Arn)> CreateQueue(FidelityContext ctx, bool fifo = false)
    {
        var baseName = XName("q");
        var name = fifo ? $"{baseName}.fifo" : baseName;
        var sqs = ctx.Sqs;
        Dictionary<string, string> createAttrs = [];
        if (fifo)
        {
            createAttrs["FifoQueue"] = "true";
        }

        var resp = await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = createAttrs,
            Tags = XsvcResourceTags(),
        });
        var url = resp.QueueUrl;
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["QueueArn"],
        })).Attributes!["QueueArn"];
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        return (name, url, arn);
    }
}
