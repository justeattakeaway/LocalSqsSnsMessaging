// Translated from behavioral/s3/check_s3_ssec_and_endpoint_enforcement_website_redirect_location_answers_301_while_rest_serves_the_body.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-ssec-and-endpoint-enforcement-website-redirect-location-answers-301-while-rest-serves-the-body</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3SsecAndEndpointEnforcementWebsiteRedirectLocationAnswers301WhileRestServesTheBody
{
    /// <summary>
    /// A WebsiteRedirectLocation object is a 301 to exactly the stored Location on the website endpoint but
    /// a 200-with-body on REST; a location neither "/..." nor http(s) is refused.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-ssec-and-endpoint-enforcement-website-redirect-location-answers-301-while-rest-serves-the-body")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        const string check = "s3-ssec-and-endpoint-enforcement-website-redirect-location-answers-301-while-rest-serves-the-body";
        var oldPage = "<!doctype html><p>old page body - only the REST endpoint ever serves this</p>"u8.ToArray();
        var extPage = "<!doctype html><p>external redirect stub body</p>"u8.ToArray();
        var newPage = "<!doctype html><p>new page</p>"u8.ToArray();
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.EdgeBucket(ctx, check);
        var referer = S3Fidelity.TokenUrlSafe();
        var withReferer = new Dictionary<string, string> { ["Referer"] = referer };
        await S3Fidelity.GrantPublicRead(s3, bucket, "PublicRead", [$"arn:aws:s3:::{bucket}/*"], referer);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "old.html", InputStream = new MemoryStream(oldPage), ContentType = "text/html", WebsiteRedirectLocation = "/new.html" });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "ext.html", InputStream = new MemoryStream(extPage), ContentType = "text/html", WebsiteRedirectLocation = "https://example.com/landing" });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "new.html", InputStream = new MemoryStream(newPage), ContentType = "text/html" });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "index.html", ContentBody = "<p>index</p>", ContentType = "text/html" });
        var stored = (await s3.GetObjectMetadataAsync(bucket, "old.html")).WebsiteRedirectLocation;
        Ensure(stored == "/new.html", $"HeadObject reports WebsiteRedirectLocation {Py.Repr(stored)}");

        // A location neither "/..." nor http(s) is refused, and nothing is written.
        var (code, status, msg) = await S3Fidelity.ErrorTriple(() => s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = "bad.html", ContentBody = "x", WebsiteRedirectLocation = "new.html",
        }));
        Ensure(
            (code, status) == ("InvalidRedirectLocation", 400),
            $"PutObject with WebsiteRedirectLocation='new.html' returned {Py.Repr(code)}/{status} ({Py.Repr(msg)});" +
            " expected InvalidRedirectLocation/400");
        Ensure(msg == "The website redirect location must have a prefix of 'http://' or 'https://' or '/'.", msg ?? "None");
        (code, _, msg) = await S3Fidelity.ErrorTriple(() => s3.GetObjectAsync(bucket, "bad.html"));
        Ensure(code == "NoSuchKey", $"the refused PutObject created bad.html ({Py.Repr(code)}: {Py.Repr(msg)})");
        Ensure(msg == "The specified key does not exist.", msg ?? "None");

        await s3.PutBucketWebsiteAsync(new PutBucketWebsiteRequest { BucketName = bucket, WebsiteConfiguration = new WebsiteConfiguration { IndexDocumentSuffix = "index.html" } });
        var baseUrl = await S3Fidelity.WebsiteBaseUrl(ctx, bucket, dashFirst: true, withReferer);
        var (_, _, raw) = await S3Fidelity.AwaitStatus(ctx, () => S3Fidelity.Http(ctx, "GET", $"{baseUrl}/new.html", withReferer), want: 200);
        Ensure(raw.SequenceEqual(newPage), $"website GET /new.html returned {S3Fidelity.Head(raw)}, expected its bytes");

        // The website endpoint answers both redirecting objects with a 301 to the stored Location.
        foreach (var method in new[] { "GET", "HEAD" })
        {
            var (redirect, headers, body) = await S3Fidelity.Http(ctx, method, $"{baseUrl}/old.html", withReferer);
            Ensure(redirect == 301, $"website {method} /old.html returned HTTP {redirect}, expected 301 — body {S3Fidelity.Head(body)}");
            Ensure(
                headers.GetValueOrDefault("location") == "/new.html",
                $"website {method} /old.html Location is {Py.Repr(headers.GetValueOrDefault("location"))}, expected " +
                "'/new.html'");
            Ensure(body.AsSpan().IndexOf(oldPage) < 0, $"website {method} /old.html served the redirecting object's body");
            (redirect, headers, body) = await S3Fidelity.Http(ctx, method, $"{baseUrl}/ext.html", withReferer);
            Ensure(
                redirect == 301 && headers.GetValueOrDefault("location") == "https://example.com/landing",
                $"website {method} /ext.html returned HTTP {redirect} Location " +
                $"{Py.Repr(headers.GetValueOrDefault("location"))}; expected 301 to 'https://example.com/landing'");
            Ensure(body.AsSpan().IndexOf(extPage) < 0, $"website {method} /ext.html served the redirecting object's body");
        }

        // The REST endpoint serves the body, and names the redirect only as a header.
        var rest = S3Fidelity.WithoutQuery(S3Fidelity.Presign(ctx, "GET", bucket, "old.html", 60, pathStyle: true));
        var (restStatus, restHeaders, restBody) = await S3Fidelity.Http(ctx, "GET", rest, withReferer);
        Ensure(restStatus == 200, $"anonymous REST GET {rest} returned HTTP {restStatus}: {S3Fidelity.Head(restBody, 300)}");
        Ensure(restBody.SequenceEqual(oldPage), $"REST GET /old.html returned {S3Fidelity.Head(restBody)}, expected the stored body");
        Ensure(
            restHeaders.GetValueOrDefault("x-amz-website-redirect-location") == "/new.html",
            "REST GET carried x-amz-website-redirect-location " +
            $"{Py.Repr(restHeaders.GetValueOrDefault("x-amz-website-redirect-location"))}, expected '/new.html'");
        Ensure(!restHeaders.ContainsKey("location"), $"the REST endpoint answered with a Location header {Py.Repr(restHeaders.GetValueOrDefault("location"))}");
    }
}
