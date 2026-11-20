// Translated from behavioral/s3/check_e2e_website_deployment.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.e2e-website-deployment</summary>
[Category("AwsEvaluationSuite")]
public sealed class E2eWebsiteDeployment
{
    /// <summary>Website endpoints use the dash form in regions that predate the dotted convention.</summary>
    private static readonly HashSet<string> LegacyDashRegions =
    [
        "us-east-1", "us-west-1", "us-west-2", "eu-west-1",
        "ap-southeast-1", "ap-southeast-2", "ap-northeast-1", "sa-east-1",
    ];

    /// <summary>
    /// The S3 website endpoint serves the index document at the root, the configured error document for
    /// a missing key, and a non-index key with its stored content-type: routing the S3 API cannot show.
    /// </summary>
    [Test]
    [DisplayName("s3.e2e-website-deployment")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;

        // Only requests carrying this per-run secret as Referer may read the website's objects.
        var referer = S3Fidelity.TokenUrlSafe();
        var bucket = await NewPublicWebsiteBucket(ctx, s3, referer);
        var htmlBody = Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body><h1>Hello</h1></body></html>");
        var errorBody = Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body><h1>Not Found</h1></body></html>");
        var cssBody = Encoding.UTF8.GetBytes("body { font-family: sans-serif; }");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "index.html", InputStream = new MemoryStream(htmlBody), ContentType = "text/html" });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "error.html", InputStream = new MemoryStream(errorBody), ContentType = "text/html" });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "style.css", InputStream = new MemoryStream(cssBody), ContentType = "text/css" });

        var endpoint = LegacyDashRegions.Contains(ctx.Region)
            ? $"http://{bucket}.s3-website-{ctx.Region}.amazonaws.com"
            : $"http://{bucket}.s3-website.{ctx.Region}.amazonaws.com";
        var withReferer = new Dictionary<string, string> { ["Referer"] = referer };

        // Root path resolves the configured IndexDocument — a plain object store has no notion of
        // "index document at /"; this is website-endpoint routing, not GetObject.
        var (status, headers, body) = await PollGet(ctx, $"{endpoint}/", withReferer, r => r.Status == 200);
        Ensure(status == 200, $"expected 200 for website root, got {status}: {S3Fidelity.Head(body, 300)}");
        Ensure(body.SequenceEqual(htmlBody), $"expected index.html body at website root, got {S3Fidelity.Head(body)}");
        Ensure(
            headers.GetValueOrDefault("content-type", "").StartsWith("text/html", StringComparison.Ordinal),
            $"expected text/html content-type at website root, got {Py.Repr(headers.GetValueOrDefault("content-type"))}");

        // A missing key must be answered by the CONFIGURED error document body, with the standing
        // HTTP status still 404 — not the default S3 XML error page.
        (status, headers, body) = await S3Fidelity.Http(ctx, "GET", $"{endpoint}/does-not-exist.html", withReferer);
        Ensure(status == 404, $"expected 404 for a missing key via the website endpoint, got {status}");
        Ensure(body.SequenceEqual(errorBody), $"expected the configured error document body, got {S3Fidelity.Head(body)}");

        // A non-index key round-trips its own stored content-type through the website endpoint.
        (status, headers, body) = await S3Fidelity.Http(ctx, "GET", $"{endpoint}/style.css", withReferer);
        Ensure(status == 200, $"expected 200 for style.css via the website endpoint, got {status}");
        Ensure(body.SequenceEqual(cssBody), $"expected style.css body, got {S3Fidelity.Head(body)}");
        Ensure(
            headers.GetValueOrDefault("content-type", "").StartsWith("text/css", StringComparison.Ordinal),
            $"expected text/css content-type for style.css, got {Py.Repr(headers.GetValueOrDefault("content-type"))}");
    }

    /// <summary>
    /// Create a uniquely-named bucket configured for static website hosting, readable anonymously
    /// by requests whose Referer is <paramref name="referer"/>. A Referer-conditioned grant still counts
    /// as public, so only BlockPublicPolicy and RestrictPublicBuckets are lifted; the ACL blocks stay on.
    /// </summary>
    private static async Task<string> NewPublicWebsiteBucket(FidelityContext ctx, Amazon.S3.IAmazonS3 s3, string referer)
    {
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await S3Fidelity.GrantPublicRead(s3, name, "PublicRead", [$"arn:aws:s3:::{name}/*"], referer);
        await s3.PutBucketWebsiteAsync(new PutBucketWebsiteRequest
        {
            BucketName = name,
            WebsiteConfiguration = new WebsiteConfiguration { IndexDocumentSuffix = "index.html", ErrorDocument = "error.html" },
        });
        return name;
    }

    /// <summary>
    /// Bounded retry: the public-read policy and website config are eventually consistent
    /// immediately after being set, so the first fetch after deployment may lag by a few seconds.
    /// </summary>
    private static async Task<(int Status, Dictionary<string, string> Headers, byte[] Body)> PollGet(
        FidelityContext ctx,
        string url,
        IReadOnlyDictionary<string, string> headers,
        Func<(int Status, Dictionary<string, string> Headers, byte[] Body), bool> ready)
    {
        var clock = FidelityTime.StartNew();
        (int, Dictionary<string, string>, byte[]) result = (0, [], []);
        while (clock.Elapsed < TimeSpan.FromSeconds(60.0))
        {
            result = await S3Fidelity.Http(ctx, "GET", url, headers);
            if (ready(result))
            {
                return result;
            }

            await ctx.Delay(TimeSpan.FromSeconds(3.0));
        }

        return result;
    }
}
