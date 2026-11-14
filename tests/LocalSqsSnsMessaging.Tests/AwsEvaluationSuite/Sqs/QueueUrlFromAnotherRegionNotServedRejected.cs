// Translated from behavioral/sqs/check_queue_url_from_another_region_not_served_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.queue-url-from-another-region-not-served-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueueUrlFromAnotherRegionNotServedRejected
{
    private const string NoSuchQueue = "AWS.SimpleQueueService.NonExistentQueue";
    private const string NoSuchQueueMsg = "The specified queue does not exist.";

    /// <summary>
    /// A home-region client handed another region's QueueUrl gets NonExistentQueue on SendMessage,
    /// ReceiveMessage, GetQueueAttributes and GetQueueUrl, and nothing reaches the far queue, because
    /// a queue is served only by its own region's endpoint.
    /// </summary>
    [Test]
    [DisplayName("sqs.queue-url-from-another-region-not-served-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var home = ctx.Sqs;
        var altRegion = OtherRegion(ctx.Region);
        var alt = ctx.Client(region: altRegion);
        var name = FidelityContext.UniqueName("sim2real-behav-xr-url-");
        var farUrl = (await alt.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Tags = new() { ["sim2real"] = "queue-url-from-another-region-not-served-rejected" },
        })).QueueUrl;
        ctx.OnCleanup(() => alt.DeleteQueueAsync(farUrl));
        // An emulator's own endpoint-relative URL carries no region (graded by scheme only).
        Ensure(farUrl.Contains(altRegion, StringComparison.Ordinal) || PublishedUrlMismatch(farUrl) is null,
            $"the '{altRegion}' queue URL does not name its region");

        var refusedBody = FidelityContext.UniqueName("refused-");
        foreach (var (label, fn) in new (string, Func<Task<object>>)[]
                 {
                     ("SendMessage", async () => await home.SendMessageAsync(new SendMessageRequest { QueueUrl = farUrl, MessageBody = refusedBody })),
                     ("ReceiveMessage", async () => await home.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = farUrl })),
                     ("GetQueueAttributes", async () => await home.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = farUrl, AttributeNames = ["QueueArn"] })),
                     ("GetQueueUrl", async () => await home.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = name })),
                 })
        {
            var err = await Error(fn);
            Ensure((err.Code, err.Message) == (NoSuchQueue, NoSuchQueueMsg),
                $"{label} on the '{ctx.Region}' endpoint for a '{altRegion}' queue answered {err}");
        }

        // Positive control and effect-absent: the far queue's own endpoint serves it, and it holds
        // only the message sent there.
        var ownBody = FidelityContext.UniqueName("own-");
        await alt.SendMessageAsync(new SendMessageRequest { QueueUrl = farUrl, MessageBody = ownBody });
        List<string> bodies = [];
        var deadline = FidelityTime.StartNew();
        while (!bodies.Contains(ownBody) && deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            var resp = await alt.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = farUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 3 });
            foreach (var m in resp.Messages ?? [])
            {
                bodies.Add(m.Body);
                await alt.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = farUrl, ReceiptHandle = m.ReceiptHandle });
            }
        }

        var tail = await alt.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = farUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 3 });
        foreach (var m in tail.Messages ?? [])
        {
            bodies.Add(m.Body);
        }

        Ensure(bodies.SequenceEqual([ownBody]),
            $"the '{altRegion}' queue delivered [{string.Join(", ", bodies)}]; expected only the message sent through "
            + $"its own endpoint, and never the one the '{ctx.Region}' endpoint refused");
    }

    private static string OtherRegion(string region) => region != "us-east-1" ? "us-east-1" : "us-west-2";

    /// <summary>The botocore Error dict <paramref name="fn"/> raises. An unexpected success raises, naming the result.</summary>
    private static async Task<AwsError> Error(Func<Task<object>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }

    /// <summary>
    /// The runner's <c>ctx.published_url_mismatch(url, labels=...)</c>, reduced to what it grades for an
    /// emulator: the URL has a host and an http(s) scheme (labels, AWS's host form, are not graded).
    /// </summary>
    private static string? PublishedUrlMismatch(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Scheme is "http" or "https" ? null : $"the published endpoint '{url}' is not http(s)"
            : $"the published data-plane endpoint is not a URL with a host: '{url}'";
}
