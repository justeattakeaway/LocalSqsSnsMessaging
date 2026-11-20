// Translated from behavioral/s3/check_s3_ssec_and_endpoint_enforcement_website_routing_rule_on_404_redirects_missing_keys.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-ssec-and-endpoint-enforcement-website-routing-rule-on-404-redirects-missing-keys</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3SsecAndEndpointEnforcementWebsiteRoutingRuleOn404RedirectsMissingKeys
{
    private const string Check = "s3-ssec-and-endpoint-enforcement-website-routing-rule-on-404-redirects-missing-keys";
    private static readonly byte[] Page = Encoding.UTF8.GetBytes("<!doctype html><p>a page that exists</p>");
    private static readonly byte[] Fallback = Encoding.UTF8.GetBytes("<!doctype html><p>fallback for anything missing</p>");

    /// <summary>
    /// A website RoutingRule on HttpErrorCodeReturnedEquals=404 redirects only missing keys, only on
    /// the website endpoint; a KeyPrefixEquals rule substitutes its prefix, and REST ignores both.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-ssec-and-endpoint-enforcement-website-routing-rule-on-404-redirects-missing-keys")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3WebsiteHostingBareFolderPathRedirectsToTrailingSlash.NewBucket(ctx, s3, Check);

        // Only requests carrying this per-run secret as Referer may read the bucket's objects.
        var referer = S3Fidelity.TokenUrlSafe();
        var withReferer = new Dictionary<string, string> { ["Referer"] = referer };
        await S3Fidelity.GrantPublicRead(s3, bucket, "PublicRead", [$"arn:aws:s3:::{bucket}/*"], referer);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "index.html", ContentBody = "<p>index</p>", ContentType = "text/html" });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "page.html", InputStream = new MemoryStream(Page), ContentType = "text/html" });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "fallback.html", InputStream = new MemoryStream(Fallback), ContentType = "text/html" });
        await s3.PutBucketWebsiteAsync(new PutBucketWebsiteRequest
        {
            BucketName = bucket,
            WebsiteConfiguration = new WebsiteConfiguration { IndexDocumentSuffix = "index.html" },
        });
        var baseUrl = await S3Fidelity.WebsiteBaseUrl(ctx, bucket, dashFirst: true, withReferer);

        // --- Control: with no rule, the website endpoint answers a missing key with a plain 404.
        await S3Fidelity.AwaitStatus(ctx, () => S3Fidelity.Http(ctx, "GET", $"{baseUrl}/page.html", withReferer), want: 200);
        var (status, headers, _) = await S3Fidelity.Http(ctx, "GET", $"{baseUrl}/missing.html", withReferer);
        Ensure(
            status == 404 && !headers.ContainsKey("location"),
            $"control: before any RoutingRule, website GET /missing.html returned HTTP {status} " +
            $"Location {Py.Repr(headers.GetValueOrDefault("location"))}; expected a plain 404");

        List<RoutingRule> rules =
        [
            new RoutingRule
            {
                Condition = new RoutingRuleCondition { KeyPrefixEquals = "docs/" },
                Redirect = new RoutingRuleRedirect { ReplaceKeyPrefixWith = "documents/" },
            },
            new RoutingRule
            {
                Condition = new RoutingRuleCondition { HttpErrorCodeReturnedEquals = "404" },
                Redirect = new RoutingRuleRedirect { ReplaceKeyWith = "fallback.html", HttpRedirectCode = "302" },
            },
        ];
        await s3.PutBucketWebsiteAsync(new PutBucketWebsiteRequest
        {
            BucketName = bucket,
            WebsiteConfiguration = new WebsiteConfiguration { IndexDocumentSuffix = "index.html", RoutingRules = rules },
        });
        var stored = (await s3.GetBucketWebsiteAsync(new GetBucketWebsiteRequest { BucketName = bucket })).WebsiteConfiguration.RoutingRules;
        Ensure(
            Describe(stored) == Describe(rules),
            $"GetBucketWebsite returned RoutingRules {Describe(stored)}, expected {Describe(rules)}");

        // --- The 404 rule fires: 302 to the absolute fallback URL on the website host, no body.
        (status, headers, _) = await S3Fidelity.AwaitStatus(ctx, () => S3Fidelity.Http(ctx, "GET", $"{baseUrl}/missing.html", withReferer), want: 302);
        Ensure(
            headers.GetValueOrDefault("location") == $"{baseUrl}/fallback.html",
            $"website GET /missing.html redirected to {Py.Repr(headers.GetValueOrDefault("location"))}, expected " +
            Py.Repr($"{baseUrl}/fallback.html"));
        (status, headers, _) = await S3Fidelity.Http(ctx, "GET", $"{baseUrl}/deeper/still-missing.html", withReferer);
        Ensure(
            status == 302 && headers.GetValueOrDefault("location") == $"{baseUrl}/fallback.html",
            $"website GET of a nested missing key returned HTTP {status} Location " +
            $"{Py.Repr(headers.GetValueOrDefault("location"))}; expected 302 to {Py.Repr($"{baseUrl}/fallback.html")}");
        var (fallbackStatus, _, raw) = await S3Fidelity.Http(ctx, "GET", headers["location"], withReferer);
        Ensure(
            fallbackStatus == 200 && raw.SequenceEqual(Fallback),
            $"following the redirect returned HTTP {fallbackStatus} {S3Fidelity.Head(raw)}; expected the fallback page");

        // --- It does NOT fire for a key that exists.
        (status, headers, raw) = await S3Fidelity.Http(ctx, "GET", $"{baseUrl}/page.html", withReferer);
        Ensure(
            status == 200 && raw.SequenceEqual(Page) && !headers.ContainsKey("location"),
            $"website GET of an EXISTING key under the 404 rule returned HTTP {status} Location " +
            $"{Py.Repr(headers.GetValueOrDefault("location"))}; expected 200 with the page bytes");

        // --- The prefix rule matches on the request key and substitutes the prefix (default 301).
        (status, headers, _) = await S3Fidelity.Http(ctx, "GET", $"{baseUrl}/docs/guide.html", withReferer);
        Ensure(
            status == 301 && headers.GetValueOrDefault("location") == $"{baseUrl}/documents/guide.html",
            $"website GET /docs/guide.html returned HTTP {status} Location {Py.Repr(headers.GetValueOrDefault("location"))};" +
            $" expected 301 to {Py.Repr($"{baseUrl}/documents/guide.html")}");

        // --- The REST endpoint ignores routing rules: the same missing key is NoSuchKey, not a redirect.
        var rest = await S3Fidelity.Error(() => s3.GetObjectAsync(bucket, "missing.html"), "the call was expected to be refused and it succeeded");
        Ensure(
            rest.Code == "NoSuchKey" && rest.StatusCode == HttpStatusCode.NotFound,
            $"REST GetObject of the missing key returned {Py.Repr(rest.Code)}/{(int)rest.StatusCode} ({Py.Repr(rest.Message)}); expected " +
            "NoSuchKey/404 — routing rules are a website-endpoint behaviour");
        Ensure(rest.Message == "The specified key does not exist.", rest.Message ?? "None");
    }

    private static string Describe(IEnumerable<RoutingRule>? rules) =>
        "[" + string.Join(", ", (rules ?? []).Select(r =>
            $"{{Condition: {{KeyPrefixEquals: {Py.Repr(r.Condition?.KeyPrefixEquals)}, HttpErrorCodeReturnedEquals: {Py.Repr(r.Condition?.HttpErrorCodeReturnedEquals)}}}, " +
            $"Redirect: {{HostName: {Py.Repr(r.Redirect?.HostName)}, Protocol: {Py.Repr(r.Redirect?.Protocol)}, ReplaceKeyPrefixWith: {Py.Repr(r.Redirect?.ReplaceKeyPrefixWith)}, " +
            $"ReplaceKeyWith: {Py.Repr(r.Redirect?.ReplaceKeyWith)}, HttpRedirectCode: {Py.Repr(r.Redirect?.HttpRedirectCode)}}}}}")) + "]";
}
