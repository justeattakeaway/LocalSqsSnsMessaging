// Translated from behavioral/s3/check_s3_website_hosting_bare_folder_path_redirects_to_trailing_slash.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-website-hosting-bare-folder-path-redirects-to-trailing-slash</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3WebsiteHostingBareFolderPathRedirectsToTrailingSlash
{
    private const string Check = "s3-website-hosting-bare-folder-path-redirects-to-trailing-slash";
    private static readonly byte[] FolderIndex = Encoding.UTF8.GetBytes("<!doctype html><title>folder index</title><p>inside folder</p>");

    /// <summary>
    /// A website-endpoint GET of a key-prefix with no trailing slash, backed by a
    /// `&lt;prefix&gt;/index.html` object, answers 302 with Location `&lt;prefix&gt;/`; the REST endpoint
    /// answers the same bare path with NoSuchKey.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-website-hosting-bare-folder-path-redirects-to-trailing-slash")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await NewBucket(ctx, s3, Check);
        var referer = S3Fidelity.TokenUrlSafe();
        var withReferer = new Dictionary<string, string> { ["Referer"] = referer };
        await S3Fidelity.GrantPublicRead(s3, bucket, "PublicReadNamedKeysOnly", [$"arn:aws:s3:::{bucket}/folder/index.html"], referer);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = "folder/index.html", InputStream = new MemoryStream(FolderIndex), ContentType = "text/html",
        });
        await s3.PutBucketWebsiteAsync(new PutBucketWebsiteRequest
        {
            BucketName = bucket,
            WebsiteConfiguration = new WebsiteConfiguration { IndexDocumentSuffix = "index.html" },
        });
        var baseUrl = await S3Fidelity.WebsiteBaseUrl(ctx, bucket, dashFirst: true, withReferer);

        var (status, headers, body) = await S3Fidelity.AwaitStatus(ctx, () => S3Fidelity.Http(ctx, "GET", $"{baseUrl}/folder", withReferer), want: 302);
        Ensure(
            headers.GetValueOrDefault("location") == "/folder/",
            $"website GET {baseUrl}/folder returned HTTP {status} Location " +
            $"{Py.Repr(headers.GetValueOrDefault("location"))}; expected a 302 to '/folder/'");
        Ensure(
            !Encoding.UTF8.GetString(body).Contains(Encoding.UTF8.GetString(FolderIndex), StringComparison.Ordinal),
            $"website GET {baseUrl}/folder (no trailing slash) served the folder index's bytes directly " +
            $"instead of redirecting: {S3Fidelity.Head(body)}");

        // --- Following the redirect serves the folder's index document.
        var location = headers["location"];
        (status, _, body) = await S3Fidelity.AwaitStatus(ctx, () => S3Fidelity.Http(ctx, "GET", $"{baseUrl}{location}", withReferer), want: 200);
        Ensure(
            status == 200 && body.SequenceEqual(FolderIndex),
            $"GET {baseUrl}{location} (the redirect target) returned HTTP {status} " +
            $"{S3Fidelity.Head(body)}; expected 200 with the folder index bytes");

        // --- REST twin, same instant: "folder" is not itself an object, so GetObject answers NoSuchKey
        // rather than any redirect.
        var rest = await S3Fidelity.Error(() => s3.GetObjectAsync(bucket, "folder"), "the call was expected to be refused and it succeeded");
        Ensure(
            rest.Code == "NoSuchKey" && rest.StatusCode == HttpStatusCode.NotFound,
            $"REST GetObject of key 'folder' returned {Py.Repr(rest.Code)}/{(int)rest.StatusCode} ({Py.Repr(rest.Message)}); expected " +
            "NoSuchKey/404 — the bare-folder redirect is a website-endpoint behaviour");
        Ensure(rest.Message == "The specified key does not exist.", rest.Message ?? "None");
    }

    /// <summary>Create a uniquely-named, tagged bucket; register policy+object+bucket teardown.</summary>
    internal static async Task<string> NewBucket(FidelityContext ctx, Amazon.S3.IAmazonS3 s3, string check)
    {
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-s3site-");
        ctx.OnCleanup(async () =>
        {
            try
            {
                await s3.DeleteBucketPolicyAsync(new DeleteBucketPolicyRequest { BucketName = name });
            }
            catch (Amazon.S3.AmazonS3Exception ex) when (ex.ErrorCode is "NoSuchBucket" or "NoSuchBucketPolicy")
            {
            }
        });
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = name,
            TagSet = [new Tag { Key = "sim2real:managed", Value = "true" }, new Tag { Key = "sim2real:check", Value = check.Length > 256 ? check[..256] : check }],
        });
        return name;
    }
}
