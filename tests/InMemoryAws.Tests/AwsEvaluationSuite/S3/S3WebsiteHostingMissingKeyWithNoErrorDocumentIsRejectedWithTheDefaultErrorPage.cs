// Translated from behavioral/s3/check_s3_website_hosting_missing_key_with_no_error_document_is_rejected_with_the_default_error_page.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-website-hosting-missing-key-with-no-error-document-is-rejected-with-the-default-error-page</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3WebsiteHostingMissingKeyWithNoErrorDocumentIsRejectedWithTheDefaultErrorPage
{
    private const string Check = "s3-website-hosting-missing-key-with-no-error-document-is-rejected-with-the-default-error-page";
    private static readonly byte[] Index = Encoding.UTF8.GetBytes("<!doctype html><title>root index</title><p>root</p>");

    /// <summary>
    /// A website-endpoint GET of a missing key, with no ErrorDocument configured, is rejected
    /// with S3's own default HTML 404 page naming Code NoSuchKey and its exact Message.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-website-hosting-missing-key-with-no-error-document-is-rejected-with-the-default-error-page")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3WebsiteHostingBareFolderPathRedirectsToTrailingSlash.NewBucket(ctx, s3, Check);
        var referer = S3Fidelity.TokenUrlSafe();
        var withReferer = new Dictionary<string, string> { ["Referer"] = referer };

        // The missing key is granted too: an ungranted key answers 403 whether or not it exists, so
        // the 404 default error page only appears for a key the policy actually covers.
        await S3Fidelity.GrantPublicRead(
            s3, bucket, "PublicReadNamedKeysOnly",
            [$"arn:aws:s3:::{bucket}/index.html", $"arn:aws:s3:::{bucket}/this-key-does-not-exist.html"], referer);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "index.html", InputStream = new MemoryStream(Index), ContentType = "text/html" });
        await s3.PutBucketWebsiteAsync(new PutBucketWebsiteRequest
        {
            BucketName = bucket,
            WebsiteConfiguration = new WebsiteConfiguration { IndexDocumentSuffix = "index.html" },
        });
        var gotCfg = (await s3.GetBucketWebsiteAsync(new GetBucketWebsiteRequest { BucketName = bucket })).WebsiteConfiguration;
        Ensure(
            gotCfg.ErrorDocument is null,
            $"GetBucketWebsite reports an ErrorDocument {Py.Repr(gotCfg.ErrorDocument)}; this check " +
            "needs none configured so the default error page is the one under test");
        var baseUrl = await S3Fidelity.WebsiteBaseUrl(ctx, bucket, dashFirst: true, withReferer);

        // --- Positive control: the website endpoint is live and serving the index document.
        await S3Fidelity.AwaitStatus(ctx, () => S3Fidelity.Http(ctx, "GET", $"{baseUrl}/", withReferer), want: 200);

        var (_, headers, body) = await S3Fidelity.AwaitStatus(
            ctx, () => S3Fidelity.Http(ctx, "GET", $"{baseUrl}/this-key-does-not-exist.html", withReferer), want: 404);
        var text = Encoding.UTF8.GetString(body);
        Ensure(
            headers.GetValueOrDefault("content-type", "").Contains("html", StringComparison.OrdinalIgnoreCase),
            $"the missing-key 404 carried Content-Type {Py.Repr(headers.GetValueOrDefault("content-type"))}; expected an " +
            "HTML error page");
        Ensure(
            !text.Contains("<?xml", StringComparison.Ordinal),
            $"the missing-key 404 returned an XML error body: {S3Fidelity.Head(body, 300)} — the website endpoint " +
            "must answer with its own HTML error page, not the REST endpoint's XML");
        Ensure(text.Contains("<h1>404 Not Found</h1>", StringComparison.Ordinal), $"the default error page is missing the <h1>404 Not Found</h1> heading: {S3Fidelity.Head(body, 500)}");
        Ensure(text.Contains("<li>Code: NoSuchKey</li>", StringComparison.Ordinal), $"the default error page is missing the documented Code line for NoSuchKey: {S3Fidelity.Head(body, 500)}");
        Ensure(
            text.Contains("<li>Message: The specified key does not exist.</li>", StringComparison.Ordinal),
            $"the default error page is missing the documented Message line: {S3Fidelity.Head(body, 500)}");

        // --- REST twin, same instant: the same missing key is a plain XML NoSuchKey, not an HTML page.
        var rest = await S3Fidelity.Error(() => s3.GetObjectAsync(bucket, "this-key-does-not-exist.html"), "the call was expected to be refused and it succeeded");
        Ensure(
            rest.Code == "NoSuchKey" && rest.StatusCode == HttpStatusCode.NotFound,
            $"REST GetObject of the same missing key returned {Py.Repr(rest.Code)}/{(int)rest.StatusCode} ({Py.Repr(rest.Message)}); " +
            "expected NoSuchKey/404");
        Ensure(rest.Message == "The specified key does not exist.", rest.Message ?? "None");
    }
}
