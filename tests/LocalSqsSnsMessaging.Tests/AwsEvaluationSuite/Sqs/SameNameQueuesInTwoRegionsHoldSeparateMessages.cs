// Translated from behavioral/sqs/check_same_name_queues_in_two_regions_hold_separate_messages.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.same-name-queues-in-two-regions-hold-separate-messages</summary>
[Category("AwsEvaluationSuite")]
public sealed class SameNameQueuesInTwoRegionsHoldSeparateMessages
{
    private const string NoSuchQueue = "AWS.SimpleQueueService.NonExistentQueue";

    private static Dictionary<string, string> Tags() => new() { ["sim2real"] = "same-name-queues-in-two-regions-hold-separate-messages" };

    /// <summary>
    /// Same-name queues in two regions of one account are separate stores: each region's
    /// GetQueueUrl answers its own URL, messages are received only where sent, and deleting the
    /// other region's queue leaves the home queue's messages intact.
    /// </summary>
    [Test]
    [DisplayName("sqs.same-name-queues-in-two-regions-hold-separate-messages")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.MultiRegion, "the 'us-east-1' queue of the same name is the home queue, so it delivers the home region's messages too")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var homeRegion = ctx.Region;
        var altRegion = OtherRegion(homeRegion);
        var home = ctx.Sqs;
        var alt = ctx.Client(region: altRegion);
        var name = FidelityContext.UniqueName("sim2real-behav-xr-msgs-");

        var homeUrl = (await home.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => DeleteQuietly(home, homeUrl));
        var altUrl = (await alt.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => DeleteQuietly(alt, altUrl));
        // An endpoint-relative URL may carry no region, so two regions may share it; an AWS host
        // must name its region.
        Ensure(homeUrl != altUrl || (PublishedUrlMismatch(homeUrl) is null && PublishedUrlMismatch(altUrl) is null),
            $"both regions returned the same URL for '{name}'");

        Ensure((await home.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = name })).QueueUrl == homeUrl,
            $"GetQueueUrl in '{homeRegion}' did not answer that region's own queue URL");
        Ensure((await alt.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = name })).QueueUrl == altUrl,
            $"GetQueueUrl in '{altRegion}' did not answer that region's own queue URL");

        Dictionary<string, string> sentHome = [];
        for (var k = 0; k < 2; k++)
        {
            var body = $"home-{k}-{name}";
            var r = await home.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = homeUrl,
                MessageBody = body,
                MessageAttributes = new() { ["origin"] = new() { DataType = "String", StringValue = homeRegion } },
            });
            sentHome[r.MessageId] = body;
        }

        var altBody = $"alt-{name}";
        var altId = (await alt.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = altUrl,
            MessageBody = altBody,
            MessageAttributes = new() { ["origin"] = new() { DataType = "String", StringValue = altRegion } },
        })).MessageId;

        var altGot = await Drain(alt, altUrl, 1);
        Ensure(altGot.Select(m => (m.MessageId, m.Body)).SequenceEqual([(altId, altBody)]),
            $"the '{altRegion}' queue must deliver exactly its own message; it delivered "
            + $"[{string.Join(", ", altGot.Select(m => m.Body))}]");
        Ensure(altGot[0].MessageAttributes!["origin"].StringValue == altRegion,
            $"the '{altRegion}' message carries origin "
            + $"'{altGot[0].MessageAttributes!["origin"].StringValue}'");

        // Delete the other region's queue; the home queue is unaffected and still holds its two.
        await alt.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = altUrl });
        var homeGot = await Drain(home, homeUrl, 2);
        var gotHome = new Dictionary<string, string>();
        foreach (var m in homeGot)
        {
            gotHome[m.MessageId] = m.Body;
        }

        Ensure(gotHome.Count == sentHome.Count && !gotHome.Except(sentHome).Any(),
            $"the '{homeRegion}' queue must deliver exactly its own two messages after the "
            + $"'{altRegion}' queue was deleted; it delivered [{string.Join(", ", gotHome.Values.Order(StringComparer.Ordinal))}]");
        var origins = homeGot.Select(m => m.MessageAttributes!["origin"].StringValue).ToHashSet();
        Ensure(origins.SetEquals([homeRegion]), $"home messages carry origins [{string.Join(", ", origins.Order(StringComparer.Ordinal))}]");
    }

    private static string OtherRegion(string region) => region != "us-east-1" ? "us-east-1" : "us-west-2";

    private static async Task<List<Message>> Drain(IAmazonSQS sqs, string url, int want, double timeoutS = 30.0)
    {
        List<Message> got = [];
        var deadline = FidelityTime.StartNew();
        while (got.Count < want && deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            got.AddRange((await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 3,
                MessageAttributeNames = ["All"],
            })).Messages ?? []);
        }

        // One more look, so a message that should not be there is seen too.
        got.AddRange((await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 2,
            MessageAttributeNames = ["All"],
        })).Messages ?? []);
        return got;
    }

    private static async Task DeleteQuietly(IAmazonSQS sqs, string url)
    {
        var err = await FidelityContext.CaptureError(() => sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = url }));
        if (err is not null && err.Code != NoSuchQueue)
        {
            throw err.Exception;
        }
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
