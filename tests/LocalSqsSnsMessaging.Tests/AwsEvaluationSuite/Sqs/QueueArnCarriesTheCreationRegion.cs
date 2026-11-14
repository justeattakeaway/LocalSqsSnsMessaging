// Translated from behavioral/sqs/check_queue_arn_carries_the_creation_region.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.queue-arn-carries-the-creation-region</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueueArnCarriesTheCreationRegion
{
    private const string AltRegion = "us-east-1";
    private const double SettleSeconds = 60.0;
    private const string NoSuchQueue = "AWS.SimpleQueueService.NonExistentQueue";

    /// <summary>
    /// A queue's ARN and URL name the region it was created in, and two same-named queues in two
    /// regions are separate resources that do not appear in each other's listing.
    /// </summary>
    [Test]
    [DisplayName("sqs.queue-arn-carries-the-creation-region")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.MultiRegion, "[alt] QueueArn must name the region the queue was created in: expected 'us-east-1', got 'us-west-2'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var homeRegion = ctx.Region;
        Ensure(homeRegion != AltRegion,
            $"this check needs two distinct regions; the harness region is '{homeRegion}' which equals " +
            $"the alternate '{AltRegion}'. Change ALT_REGION");
        var home = ctx.Sqs;
        var alt = ctx.Client(region: AltRegion);

        // Two explicit regions and one reused name: a hardcoded region or a store not partitioned by
        // region is invisible to a single-region run.
        var sharedName = FidelityContext.UniqueName("sim2real-behav-region-q-");

        var homeUrl = (await home.CreateQueueAsync(new CreateQueueRequest { QueueName = sharedName, Tags = new() { ["sim2real:managed"] = "true" } })).QueueUrl;
        ctx.OnCleanup(() => DeleteIfPresent(home, homeUrl));
        var altUrl = (await alt.CreateQueueAsync(new CreateQueueRequest { QueueName = sharedName, Tags = new() { ["sim2real:managed"] = "true" } })).QueueUrl;
        ctx.OnCleanup(() => DeleteIfPresent(alt, altUrl));

        // Each ARN names its own region.
        foreach (var (label, client, url, expectedRegion) in new[]
                 {
                     ("home", home, homeUrl, homeRegion),
                     ("alt", alt, altUrl, AltRegion),
                 })
        {
            var arn = (await client.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
            var parts = arn.Split(':');
            Ensure(parts.Length == 6 && parts[0] == "arn" && parts[2] == "sqs", $"[{label}] malformed QueueArn '{arn}'");
            Ensure(parts[3] == expectedRegion,
                $"[{label}] QueueArn must name the region the queue was created in: expected " +
                $"'{expectedRegion}', got '{parts[3]}' (full ARN '{arn}'). A target that hardcodes one " +
                $"region reports the same value for both queues and fails on whichever is not that region");
            Ensure(parts[5] == sharedName, $"[{label}] QueueArn must end in the queue name '{sharedName}', got '{arn}'");
        }

        // The URL is regional too.
        // An AWS host must name the region; a host an emulator published as its own carries none, so
        // those URLs are graded through ctx.published_url_mismatch (scheme only).
        Ensure(homeUrl != altUrl || (PublishedUrlMismatch(homeUrl) is null && PublishedUrlMismatch(altUrl) is null),
            $"two queues in two regions must have distinct URLs; both were '{homeUrl}'");
        Ensure((homeUrl.Contains(homeRegion, StringComparison.Ordinal) && altUrl.Contains(AltRegion, StringComparison.Ordinal))
                || (PublishedUrlMismatch(homeUrl) is null && PublishedUrlMismatch(altUrl) is null),
            $"each QueueUrl must carry its own region: '{homeUrl}' / '{altUrl}'");

        // Region is a state boundary: neither queue appears in the other's listing.
        // The two queues share a name, so a target that does not partition by region can only show one of
        // them.
        var homeNames = await QueueNames(home, sharedName);
        var altNames = await QueueNames(alt, sharedName);
        Ensure(homeNames.SetEquals([sharedName]),
            $"ListQueues in '{homeRegion}' must show exactly the queue created there, got [{string.Join(", ", homeNames)}]");
        Ensure(altNames.SetEquals([sharedName]),
            $"ListQueues in '{AltRegion}' must show exactly the queue created there, got [{string.Join(", ", altNames)}]");

        // Deleting one leaves the other.
        await alt.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = altUrl });
        await AwaitAbsent(alt, sharedName);
        var stillThere = (await home.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = homeUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        Ensure(stillThere.Split(':')[3] == homeRegion,
            $"deleting the '{AltRegion}' queue must not disturb the '{homeRegion}' one; its ARN now reads '{stillThere}'");
    }

    /// <summary>
    /// <c>ctx.published_url_mismatch(url, labels=...)</c>: why a URL the emulator published is wrong,
    /// or null when it is right. Ported as the scheme-and-host check; the runner's own-host
    /// (<c>OwnHosts.problem</c>) test has no counterpart for the in-memory bus.
    /// </summary>
    private static string? PublishedUrlMismatch(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parts) || string.IsNullOrEmpty(parts.Host))
        {
            return $"the published data-plane endpoint is not a URL with a host: '{url}'";
        }

        return parts.Scheme is "http" or "https" ? null : $"the published endpoint '{url}' is not http(s)";
    }

    /// <summary>Queue names in this region with the given unique-name prefix, paginated.</summary>
    private static async Task<HashSet<string>> QueueNames(AmazonSQSClient sqs, string prefixName)
    {
        var found = new HashSet<string>();
        string? nextToken = null;
        do
        {
            var page = await sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = prefixName, NextToken = nextToken });
            foreach (var url in page.QueueUrls ?? [])
            {
                found.Add(url[(url.LastIndexOf('/') + 1)..]);
            }

            nextToken = page.NextToken;
        }
        while (!string.IsNullOrEmpty(nextToken));

        return found;
    }

    /// <summary>Wait for the queue to leave ListQueues; SQS deletion can take up to 60s to show.</summary>
    private static async Task AwaitAbsent(AmazonSQSClient sqs, string name)
    {
        var deadline = FidelityTime.StartNew();
        var last = new HashSet<string>();
        while (deadline.Elapsed < TimeSpan.FromSeconds(SettleSeconds))
        {
            last = await QueueNames(sqs, name);
            if (last.Count == 0)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3.0));
        }

        throw Fail($"queue '{name}' still listed {SettleSeconds}s after delete (last=[{string.Join(", ", last)}])");
    }

    private static async Task DeleteIfPresent(AmazonSQSClient sqs, string url)
    {
        var error = await FidelityContext.CaptureError(() => sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = url }));
        if (error is not null && error.Code != NoSuchQueue)
        {
            throw error.Exception;
        }
    }
}
