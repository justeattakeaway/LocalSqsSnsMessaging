// Translated from behavioral/sqs/check_cli_query_jmespath_filter.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.cli-query-jmespath-filter</summary>
[Category("AwsEvaluationSuite")]
public sealed class CliQueryJmespathFilter
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// --query applies a JMESPath filter expression ([?...]) to a list result.
    /// </summary>
    [Test]
    [DisplayName("sqs.cli-query-jmespath-filter")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var prefix = XName("q");
        var urls = new Dictionary<string, string>();
        foreach (var suffix in new[] { "alpha", "beta", "target" })
        {
            var name = $"{prefix}-{suffix}";
            var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = XsvcResourceTags() })).QueueUrl;
            urls[suffix] = url;
            ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        }

        // ListQueues can lag CreateQueue; poll briefly until all three are visible.
        List<string> allUrls = [];
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(20.0))
        {
            allUrls = (await sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = prefix })).QueueUrls ?? [];
            if (allUrls.Count >= 3)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(allUrls.Count == 3, $"Expected 3 queues under prefix '{prefix}', got [{string.Join(", ", allUrls)}]");

        // A JMESPath filter expression ([?...]) narrows the list result to the one match.
        // `aws sqs list-queues --queue-name-prefix PREFIX --query "QueueUrls[?ends_with(@, 'target')]"`:
        // the ListQueues call, with the JMESPath filter (client-side in the CLI) applied here.
        var filtered = await ListQueuesFiltered(ctx, prefix, "target");
        Ensure(filtered is not null && filtered.SequenceEqual([urls["target"]]),
            $"Expected [{urls["target"]}], got {Show(filtered)}");

        // The same filter on a suffix that matches nothing narrows to empty, not to everything.
        var unmatched = await ListQueuesFiltered(ctx, prefix, "no-such-suffix");
        Ensure(unmatched is not null && unmatched.Count == 0, $"Expected no matches, got {Show(unmatched)}");
    }

    /// <summary>
    /// ListQueues then <c>QueueUrls[?ends_with(@, suffix)]</c>; JMESPath projects a missing
    /// <c>QueueUrls</c> to null.
    /// </summary>
    private static async Task<List<string>?> ListQueuesFiltered(FidelityContext ctx, string prefix, string suffix)
    {
        var resp = await ctx.Sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = prefix });
        return resp.QueueUrls?.Where(u => u.EndsWith(suffix, StringComparison.Ordinal)).ToList();
    }

    private static string Show(List<string>? list) => list is null ? "None" : $"[{string.Join(", ", list)}]";

    private static Dictionary<string, string> XsvcResourceTags() => new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");
}
