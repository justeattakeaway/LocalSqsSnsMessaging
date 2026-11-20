// Translated from behavioral/s3/check_s3_ssec_and_endpoint_enforcement_website_endpoint_serving_an_ssec_object_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-ssec-and-endpoint-enforcement-website-endpoint-serving-an-ssec-object-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3SsecAndEndpointEnforcementWebsiteEndpointServingAnSsecObjectRejected
{
    /// <summary>
    /// The plaintext website endpoint refuses an SSE-C object with 400 InvalidRequest (HTML) and never its
    /// bytes; a plain object is served and the SSE-C object stays readable with its key.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-ssec-and-endpoint-enforcement-website-endpoint-serving-an-ssec-object-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        const string check = "s3-ssec-and-endpoint-enforcement-website-endpoint-serving-an-ssec-object-rejected";
        var plainPage = "<!doctype html><p>plain public page</p>"u8.ToArray();
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.EdgeBucket(ctx, check);
        await S3Fidelity.AllowSsec(s3, bucket);
        var referer = S3Fidelity.TokenUrlSafe();
        var withReferer = new Dictionary<string, string> { ["Referer"] = referer };
        await S3Fidelity.GrantPublicRead(s3, bucket, "PublicRead", [$"arn:aws:s3:::{bucket}/*"], referer);
        var key = RandomNumberGenerator.GetBytes(32);
        byte[] secret = [.. "customer-encrypted bytes no anonymous caller may ever see "u8, .. RandomNumberGenerator.GetBytes(64)];
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "plain.html", InputStream = new MemoryStream(plainPage), ContentType = "text/html" });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "index.html", ContentBody = "<p>index</p>", ContentType = "text/html" });
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = "secret.html",
            InputStream = new MemoryStream(secret),
            ContentType = "text/html",
            ServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256,
            ServerSideEncryptionCustomerProvidedKey = S3Fidelity.B64(key),
        });
        await s3.PutBucketWebsiteAsync(new PutBucketWebsiteRequest { BucketName = bucket, WebsiteConfiguration = new WebsiteConfiguration { IndexDocumentSuffix = "index.html" } });
        var baseUrl = await S3Fidelity.WebsiteBaseUrl(ctx, bucket, dashFirst: true, withReferer);

        // Control: the website endpoint serves a plain object.
        var (_, _, raw) = await S3Fidelity.AwaitStatus(ctx, () => S3Fidelity.Http(ctx, "GET", $"{baseUrl}/plain.html", withReferer), want: 200);
        Ensure(raw.SequenceEqual(plainPage), $"website GET /plain.html returned {S3Fidelity.Head(raw)}, expected its bytes");

        // The SSE-C object: a 400 HTML page naming InvalidRequest, never its bytes.
        var (status, _, page) = await S3Fidelity.Http(ctx, "GET", $"{baseUrl}/secret.html", withReferer);
        Ensure(status == 400, $"website GET of an SSE-C object returned HTTP {status}, expected 400 — body {S3Fidelity.Head(page, 300)}");
        var fields = S3Fidelity.HtmlErrorFields(page);
        Ensure(fields.GetValueOrDefault("Code") == "InvalidRequest", $"the website endpoint's 400 page does not name InvalidRequest: {S3Fidelity.Head(page, 400)}");
        Ensure(fields.GetValueOrDefault("Message") == S3Fidelity.StoredSse, fields.GetValueOrDefault("Message") ?? "None");
        Ensure(page.AsSpan().IndexOf(secret.AsSpan(0, 40)) < 0, "the website endpoint served SSE-C object bytes anonymously");

        // The anonymous REST GET is refused the same way, in XML.
        var rest = S3Fidelity.WithoutQuery(S3Fidelity.Presign(ctx, "GET", bucket, "secret.html", 60, pathStyle: true));
        (status, _, raw) = await S3Fidelity.Http(ctx, "GET", rest, withReferer);
        Ensure(
            status == 400 && S3Fidelity.XmlCodeMessage(raw).Code == "InvalidRequest",
            $"anonymous REST GET of the SSE-C object returned HTTP {status} code {Py.Repr(S3Fidelity.XmlCodeMessage(raw).Code)}; " +
            "expected 400 InvalidRequest");
        Ensure(S3Fidelity.XmlCodeMessage(raw).Message == S3Fidelity.StoredSse, S3Fidelity.XmlCodeMessage(raw).Message ?? "None");
        Ensure(raw.AsSpan().IndexOf(secret.AsSpan(0, 40)) < 0, "the anonymous REST GET served SSE-C object bytes");

        // With its key, the SSE-C object is still readable.
        using var got = await s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = "secret.html",
            ServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256,
            ServerSideEncryptionCustomerProvidedKey = S3Fidelity.B64(key),
        });
        Ensure((await S3Fidelity.ReadBody(got)).SequenceEqual(secret), "the SSE-C object is not readable with its key");
    }
}
