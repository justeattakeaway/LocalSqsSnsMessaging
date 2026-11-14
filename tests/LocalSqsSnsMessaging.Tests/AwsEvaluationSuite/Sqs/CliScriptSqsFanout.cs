// Translated from behavioral/sqs/check_cli_script_sqs_fanout.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.cli-script-sqs-fanout</summary>
[Category("AwsEvaluationSuite")]
public sealed class CliScriptSqsFanout
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// Bash script sends N messages then drains and counts them.
    /// </summary>
    [Test]
    [DisplayName("sqs.cli-script-sqs-fanout")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var queue = await CreateQueue(ctx);
        var sqs = ctx.Sqs;

        // The bash script, with each `aws sqs ...` invocation replaced by the SDK call it makes.
        var queueUrl = queue.Url;
        for (var i = 1; i <= 5; i++)
        {
            await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = queueUrl, MessageBody = $"item-{i}" });
        }

        var count = 0;
        var bodies = new List<string>();
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
            var n = msgs.Count;
            if (n == 0)
            {
                break;
            }

            count += n;
            foreach (var m in msgs)
            {
                bodies.Add(m.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = m.ReceiptHandle });
            }
        }

        // `--query 'Attributes.ApproximateNumberOfMessages' --output text` prints "None" when absent.
        var depth = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl, AttributeNames = ["ApproximateNumberOfMessages"],
        })).Attributes?.GetValueOrDefault("ApproximateNumberOfMessages") ?? "None";

        // Dropped: the "no COUNT/DEPTH line in script output" asserts, which only test that the
        // script printed its markers.
        Ensure(count == 5, $"Expected 5 messages drained, got {count}");
        var expectedBodies = Enumerable.Range(1, 5).Select(i => $"item-{i}").ToHashSet();
        Ensure(bodies.ToHashSet().SetEquals(expectedBodies),
            $"Expected drained bodies [{string.Join(", ", expectedBodies.Order(StringComparer.Ordinal))}], got [{string.Join(", ", bodies.Order(StringComparer.Ordinal))}]");
        Ensure(depth == "0", $"Expected queue depth 0 after drain (independent GetQueueAttributes read), got '{depth}'");
    }

    private static Dictionary<string, string> XsvcResourceTags() => new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>Create an SQS queue (registers delete_queue teardown). Returns {name,url,arn}.</summary>
    private static async Task<(string Name, string Url, string Arn)> CreateQueue(FidelityContext ctx, bool fifo = false)
    {
        var baseName = XName("q");
        var name = fifo ? $"{baseName}.fifo" : baseName;
        var sqs = ctx.Sqs;
        var createAttrs = new Dictionary<string, string>();
        if (fifo)
        {
            createAttrs["FifoQueue"] = "true";
        }

        var resp = await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = createAttrs, Tags = XsvcResourceTags() });
        var url = resp.QueueUrl;
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        return (name, url, arn);
    }
}
