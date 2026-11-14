// Translated from behavioral/sqs/check_cli_output_text_mode.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.cli-output-text-mode</summary>
[Category("AwsEvaluationSuite")]
public sealed class CliOutputTextMode
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// --output text on a multi-field --query emits one line, fields tab-separated.
    /// </summary>
    [Test]
    [DisplayName("sqs.cli-output-text-mode")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var queue = await CreateQueue(ctx);
        // `aws sqs get-queue-attributes --queue-url URL --attribute-names QueueArn ApproximateNumberOfMessages
        //  --output text --query "[Attributes.QueueArn, Attributes.ApproximateNumberOfMessages]"` is this
        // GetQueueAttributes call; JMESPath yields None (printed "None") for an absent attribute.
        var resp = await ctx.Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queue.Url,
            AttributeNames = ["QueueArn", "ApproximateNumberOfMessages"],
        });
        var fields = new[]
        {
            resp.Attributes?.GetValueOrDefault("QueueArn") ?? "None",
            resp.Attributes?.GetValueOrDefault("ApproximateNumberOfMessages") ?? "None",
        };
        // Dropped: `len(fields) == 2` on the tab-split stdout, which tests the CLI's text formatter
        // rather than the service.
        var (arn, count) = (fields[0], fields[1]);
        Ensure(arn == queue.Arn, $"Expected arn {queue.Arn}, got '{arn}'");
        Ensure(count == "0", $"Expected a freshly created queue to report 0, got '{count}'");
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
