// Translated from behavioral/s3/check_s3_website_hosting_redirect_all_requests_to_answers_301_to_the_target_host.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-website-hosting-redirect-all-requests-to-answers-301-to-the-target-host</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3WebsiteHostingRedirectAllRequestsToAnswers301ToTheTargetHost
{
    private const string Check = "s3-website-hosting-redirect-all-requests-to-answers-301-to-the-target-host";
    private static readonly byte[] TargetIndex = Encoding.UTF8.GetBytes("<!doctype html><title>target index</title><p>the real content</p>");
    private static readonly byte[] TargetPage = Encoding.UTF8.GetBytes("<!doctype html><title>target page</title><p>a specific page</p>");

    /// <summary>
    /// A website bucket configured with RedirectAllRequestsTo answers every path with 301 to the
    /// target bucket's website host, preserving the request path, never serving its own content.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-website-hosting-redirect-all-requests-to-answers-301-to-the-target-host")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var target = await S3WebsiteHostingBareFolderPathRedirectsToTrailingSlash.NewBucket(ctx, s3, Check);
        var source = await S3WebsiteHostingBareFolderPathRedirectsToTrailingSlash.NewBucket(ctx, s3, Check);
        var referer = S3Fidelity.TokenUrlSafe();
        var withReferer = new Dictionary<string, string> { ["Referer"] = referer };
        await S3Fidelity.GrantPublicRead(
            s3, target, "PublicReadNamedKeysOnly", [$"arn:aws:s3:::{target}/index.html", $"arn:aws:s3:::{target}/page.html"], referer);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = target, Key = "index.html", InputStream = new MemoryStream(TargetIndex), ContentType = "text/html" });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = target, Key = "page.html", InputStream = new MemoryStream(TargetPage), ContentType = "text/html" });
        await s3.PutBucketWebsiteAsync(new PutBucketWebsiteRequest
        {
            BucketName = target,
            WebsiteConfiguration = new WebsiteConfiguration { IndexDocumentSuffix = "index.html" },
        });
        var targetBase = await S3Fidelity.WebsiteBaseUrl(ctx, target, dashFirst: true, withReferer);
        var targetHost = targetBase.Split("://", 2)[1];

        // The source bucket is never read anonymously: RedirectAllRequestsTo answers every request
        // from the redirect alone, so the source needs no public grant and no index document.
        await s3.PutBucketWebsiteAsync(new PutBucketWebsiteRequest
        {
            BucketName = source,
            WebsiteConfiguration = new WebsiteConfiguration { RedirectAllRequestsTo = new RoutingRuleRedirect { HostName = targetHost } },
        });

        // TRANSPORT DERIVATION ONLY: the source serves no content of its own, so discovery probes for a 301.
        var sourceBase = await S3Fidelity.WebsiteBaseUrl(ctx, source, dashFirst: true);

        // --- The bucket root redirects to the target host's root, preserving scheme and path.
        var (status, headers, _) = await S3Fidelity.AwaitStatus(ctx, () => S3Fidelity.Http(ctx, "GET", $"{sourceBase}/"), want: 301);
        Ensure(
            headers.GetValueOrDefault("location") == $"{targetBase}/",
            $"website GET {sourceBase}/ returned HTTP {status} Location " +
            $"{Py.Repr(headers.GetValueOrDefault("location"))}; expected a 301 to {Py.Repr(targetBase + "/")}");

        // --- A specific path is preserved across the redirect, including a path the source bucket
        // never held any object for — the redirect fires unconditionally, not only for missing keys.
        (status, headers, _) = await S3Fidelity.Http(ctx, "GET", $"{sourceBase}/page.html");
        Ensure(
            status == 301 && headers.GetValueOrDefault("location") == $"{targetBase}/page.html",
            $"website GET {sourceBase}/page.html returned HTTP {status} Location " +
            $"{Py.Repr(headers.GetValueOrDefault("location"))}; expected a 301 to {Py.Repr(targetBase + "/page.html")}");
        (status, headers, _) = await S3Fidelity.Http(ctx, "GET", $"{sourceBase}/never-existed-anywhere.html");
        Ensure(
            status == 301 && headers.GetValueOrDefault("location") == $"{targetBase}/never-existed-anywhere.html",
            $"website GET of a key the target never held returned HTTP {status} Location " +
            $"{Py.Repr(headers.GetValueOrDefault("location"))}; RedirectAllRequestsTo must redirect unconditionally, " +
            "never answer its own 404");

        // --- Following the redirect reaches the target bucket's real content.
        var (pageStatus, _, body) = await S3Fidelity.AwaitStatus(ctx, () => S3Fidelity.Http(ctx, "GET", $"{targetBase}/page.html", withReferer), want: 200);
        Ensure(
            pageStatus == 200 && body.SequenceEqual(TargetPage),
            $"GET {targetBase}/page.html (the redirect target) returned HTTP {pageStatus} " +
            $"{S3Fidelity.Head(body)}; expected 200 with the target page bytes");
    }
}
