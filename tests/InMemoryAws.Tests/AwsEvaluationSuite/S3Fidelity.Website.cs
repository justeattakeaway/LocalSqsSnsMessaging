using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>What the S3 website endpoint ports share.</summary>
public static partial class S3Fidelity
{
    /// <summary>
    /// <c>_website_base_url(ctx, bucket)</c>: the bucket's website base URL, from the documented
    /// candidate hostnames (<c>s3-website-region</c> and <c>s3-website.region</c>, plain HTTP), the
    /// first that answers <c>GET /</c>. Both answer here, so the first candidate is chosen.
    /// </summary>
    public static async Task<string> WebsiteBaseUrl(FidelityContext ctx, string bucket, bool dashFirst, IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        string[] candidates = dashFirst
            ? [$"http://{bucket}.s3-website-{ctx.Region}.amazonaws.com", $"http://{bucket}.s3-website.{ctx.Region}.amazonaws.com"]
            : [$"http://{bucket}.s3-website.{ctx.Region}.amazonaws.com", $"http://{bucket}.s3-website-{ctx.Region}.amazonaws.com"];
        foreach (var candidate in candidates)
        {
            // Discovery only: any HTTP answer means a website endpoint is listening at the candidate.
            await Http(ctx, "GET", $"{candidate}/", headers);
            return candidate;
        }

        throw Fail($"none of the website endpoint candidates for bucket {bucket} could be reached: [{string.Join(", ", candidates)}]");
    }

    /// <summary>
    /// <c>_await(fetch, want=...)</c>: repeats <paramref name="fetch"/> until it answers HTTP
    /// <paramref name="want"/>; a different stable answer fails the check.
    /// </summary>
    public static async Task<(int Status, Dictionary<string, string> Headers, byte[] Body)> AwaitStatus(
        FidelityContext ctx,
        Func<Task<(int Status, Dictionary<string, string> Headers, byte[] Body)>> fetch,
        int want,
        double capSeconds = 90.0,
        double intervalSeconds = 3.0)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(fetch);
        var budget = Math.Max(1.0, Math.Min(capSeconds, ctx.TimeLeftSeconds - 5.0));
        var clock = FidelityTime.StartNew();
        var last = await fetch();
        while (last.Status != want)
        {
            if (clock.Elapsed.TotalSeconds > budget)
            {
                throw Fail(
                    $"never answered HTTP {want} within {budget:F0}s; last was HTTP {last.Status} " +
                    $"{Py.Repr(System.Text.Encoding.UTF8.GetString(last.Body.Take(300).ToArray()))}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
            last = await fetch();
        }

        return last;
    }

    /// <summary>A body as Python's <c>repr</c> of its first bytes shows it, for failure messages.</summary>
    public static string Head(byte[] body, int length = 200)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Py.Repr(System.Text.Encoding.UTF8.GetString(body.Take(length).ToArray()));
    }
}
