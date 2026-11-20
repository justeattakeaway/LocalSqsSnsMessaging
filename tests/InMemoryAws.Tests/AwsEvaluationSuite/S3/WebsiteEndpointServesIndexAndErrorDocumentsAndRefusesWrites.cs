// Translated from behavioral/s3/check_website_endpoint_serves_index_and_error_documents_and_refuses_writes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.website-endpoint-serves-index-and-error-documents-and-refuses-writes</summary>
[Category("AwsEvaluationSuite")]
public sealed class WebsiteEndpointServesIndexAndErrorDocumentsAndRefusesWrites
{
    private const string Check = "website-endpoint-serves-index-and-error-documents-and-refuses-writes";
    private static readonly byte[] Index = Encoding.UTF8.GetBytes("<!doctype html><title>root index</title><p>root</p>");
    private static readonly byte[] SubIndex = Encoding.UTF8.GetBytes("<!doctype html><title>sub index</title><p>sub</p>");
    private static readonly byte[] ErrorDoc = Encoding.UTF8.GetBytes("<!doctype html><title>custom error</title><p>not here</p>");
    private static readonly byte[] Private = Encoding.UTF8.GetBytes("this object is explicitly denied to anonymous callers");

    /// <summary>
    /// The S3 website endpoint serves `/` and `sub/` as their index documents, a missing key as the custom
    /// error document with 404, an ungranted key with 403 and a PUT with 405 that writes nothing, while the
    /// REST endpoint lists the keys at the bucket root and answers the missing key with NoSuchKey.
    /// </summary>
    [Test]
    [DisplayName("s3.website-endpoint-serves-index-and-error-documents-and-refuses-writes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = bucket,
            TagSet = [new Tag { Key = "sim2real:managed", Value = "true" }, new Tag { Key = "sim2real:check", Value = Check }],
        });

        // The missing key "definitely-not-here.html" is granted too: S3 answers an anonymous GET for a
        // key the policy does not cover with 403 whether or not it exists, so the error document's 404
        // only appears for an authorized key. The grant requires a per-run secret Referer.
        var referer = S3Fidelity.TokenUrlSafe();
        await S3Fidelity.GrantPublicRead(
            s3,
            bucket,
            "PublicReadNamedKeysOnly",
            [.. new[] { "index.html", "sub/index.html", "error.html", "definitely-not-here.html" }.Select(k => $"arn:aws:s3:::{bucket}/{k}")],
            referer);
        var withReferer = new Dictionary<string, string> { ["Referer"] = referer };
        await PutHtml(s3, bucket, "index.html", Index, "text/html");
        await PutHtml(s3, bucket, "sub/index.html", SubIndex, "text/html");
        await PutHtml(s3, bucket, "error.html", ErrorDoc, "text/html");
        await PutHtml(s3, bucket, "private.txt", Private, "text/plain");
        await s3.PutBucketWebsiteAsync(new PutBucketWebsiteRequest
        {
            BucketName = bucket,
            WebsiteConfiguration = new WebsiteConfiguration { IndexDocumentSuffix = "index.html", ErrorDocument = "error.html" },
        });
        var gotCfg = (await s3.GetBucketWebsiteAsync(new GetBucketWebsiteRequest { BucketName = bucket })).WebsiteConfiguration;
        Ensure(gotCfg.IndexDocumentSuffix == "index.html", $"GetBucketWebsite reports index suffix {Py.Repr(gotCfg.IndexDocumentSuffix)}, expected index.html");
        Ensure(gotCfg.ErrorDocument == "error.html", $"GetBucketWebsite reports error document {Py.Repr(gotCfg.ErrorDocument)}, expected error.html");

        // The documented website hosts, dot form first (HTTP-only: no SSL on AWS's website endpoints).
        var baseUrl = await S3Fidelity.WebsiteBaseUrl(ctx, bucket, dashFirst: false);

        // --- Leg 1: the root path serves the index document. On the REST endpoint the same path is a
        // key listing, which is the twin control below.
        var (_, headers, body) = await S3Fidelity.AwaitStatus(ctx, () => S3Fidelity.Http(ctx, "GET", $"{baseUrl}/", withReferer), want: 200);
        Ensure(
            body.SequenceEqual(Index),
            $"GET {baseUrl}/ returned {S3Fidelity.Head(body)}; the website endpoint must return the index " +
            "document's exact bytes");
        Ensure(
            headers.GetValueOrDefault("content-type", "").Contains("html", StringComparison.Ordinal),
            $"GET {baseUrl}/ returned Content-Type {Py.Repr(headers.GetValueOrDefault("content-type"))}; the index document was " +
            "stored as text/html");

        // --- Leg 2: a directory-shaped path appends the index suffix.
        var (status, _, subBody) = await S3Fidelity.Http(ctx, "GET", $"{baseUrl}/sub/", withReferer);
        Ensure(status == 200, $"GET {baseUrl}/sub/ returned {status}, expected 200");
        Ensure(
            subBody.SequenceEqual(SubIndex),
            $"GET {baseUrl}/sub/ returned {S3Fidelity.Head(subBody)}; expected the bytes of sub/index.html " +
            "— the index suffix is not being appended to a prefix request");

        // --- Leg 3: a missing key returns the CUSTOM ERROR DOCUMENT with a 404, not an XML error.
        var (missingStatus, _, missingBody) = await S3Fidelity.Http(ctx, "GET", $"{baseUrl}/definitely-not-here.html", withReferer);
        Ensure(
            missingStatus == 404,
            $"GET of a missing key on the website endpoint returned {missingStatus}, expected 404; body " +
            S3Fidelity.Head(missingBody));
        Ensure(
            missingBody.SequenceEqual(ErrorDoc),
            $"GET of a missing key returned {S3Fidelity.Head(missingBody)}; the website endpoint must return the " +
            "configured error document's exact bytes, not an XML error");
        var missingText = Encoding.UTF8.GetString(missingBody);
        Ensure(
            !missingText.Contains("<Error>", StringComparison.Ordinal) && !missingText.Contains("NoSuchKey", StringComparison.Ordinal),
            $"the website endpoint returned an XML error body for a missing key: {S3Fidelity.Head(missingBody)} — this " +
            "is the REST endpoint's behaviour, so the website hostname is being served by the REST " +
            "handler");

        // --- Leg 4: only publicly readable content is served. private.txt exists and is byte-identical
        // to the three public keys in every respect except that the bucket policy never granted it.
        Ensure(
            (await S3Fidelity.GetBytes(s3, bucket, "private.txt")).SequenceEqual(Private),
            "positive control: private.txt must be readable with credentials, otherwise the 403 below " +
            "would only prove the key is missing");
        var (privateStatus, _, privateBody) = await S3Fidelity.Http(ctx, "GET", $"{baseUrl}/private.txt", withReferer);
        Ensure(
            privateStatus == 403,
            $"GET of a key the bucket policy does not grant returned {privateStatus} on the website endpoint, " +
            $"expected 403; body {S3Fidelity.Head(privateBody)}");
        Ensure(
            !Encoding.UTF8.GetString(privateBody).Contains(Encoding.UTF8.GetString(Private), StringComparison.Ordinal),
            "the website endpoint served the bytes of a key no policy statement grants anonymously");

        // --- Leg 5: writes are refused. The website endpoint supports GET and HEAD only.
        var (putStatus, _, putBody) = await S3Fidelity.Http(
            ctx, "PUT", $"{baseUrl}/written-via-website.txt", withReferer, Encoding.UTF8.GetBytes("should not land"));
        Ensure(
            putStatus == 405,
            $"PUT to the website endpoint returned {putStatus}, expected 405 — the website endpoint " +
            $"supports only GET and HEAD; body {S3Fidelity.Head(putBody)}");

        // HeadObject has no body, so S3's answer is the bare status: botocore reports it as code "404"
        // with the HTTP reason phrase as the message.
        var headErr = await S3Fidelity.Error(() => s3.GetObjectMetadataAsync(bucket, "written-via-website.txt"), "the call was expected to fail and it succeeded");
        Ensure(
            S3Fidelity.HeadError(headErr) == ("404", "Not Found") && headErr.StatusCode == HttpStatusCode.NotFound,
            $"the key written-via-website.txt exists after a PUT the website endpoint refused: {S3Fidelity.Repr(S3Fidelity.HeadError(headErr))}");

        // --- Twin control, same instant: the REST endpoint answers the SAME two paths differently.
        var restKeys = ((await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket })).S3Objects ?? []).Select(o => o.Key).ToHashSet();
        Ensure(
            restKeys.SetEquals(["index.html", "sub/index.html", "error.html", "private.txt"]),
            $"the REST endpoint lists [{string.Join(", ", restKeys.Order(StringComparer.Ordinal).Select(Py.Repr))}] at the bucket root; expected the four keys " +
            "this check created — the REST root must be a key listing while the website root is the " +
            "index document");
        var getErr = await S3Fidelity.Error(() => s3.GetObjectAsync(bucket, "definitely-not-here.html"), "the call was expected to fail and it succeeded");
        Ensure(
            (getErr.Code, getErr.Message, getErr.StatusCode) == ("NoSuchKey", "The specified key does not exist.", HttpStatusCode.NotFound),
            $"the REST endpoint answered {S3Fidelity.Repr((getErr.Code, getErr.Message))} for the same missing key the website endpoint " +
            "answered with the error document; expected NoSuchKey");
    }

    private static Task PutHtml(Amazon.S3.IAmazonS3 s3, string bucket, string key, byte[] body, string contentType) =>
        s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body), ContentType = contentType });
}
