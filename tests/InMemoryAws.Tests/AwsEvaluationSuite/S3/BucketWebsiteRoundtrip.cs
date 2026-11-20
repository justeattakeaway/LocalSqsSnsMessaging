// Translated from behavioral/s3/check_bucket_website_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-website-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketWebsiteRoundtrip
{
    /// <summary>
    /// PutBucketWebsite persists the website config; DeleteBucketWebsite then GetBucketWebsite
    /// returns NoSuchWebsiteConfiguration.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-website-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await PutIndexAndError(s3, bucket);
        var cfg = await GetWebsite(s3, bucket);
        Ensure(cfg.IndexDocumentSuffix == "index.html", $"index doc not preserved: {Py.Repr(cfg.IndexDocumentSuffix)}");
        Ensure(cfg.ErrorDocument == "error.html", $"error doc not preserved: {Py.Repr(cfg.ErrorDocument)}");
        await s3.DeleteBucketWebsiteAsync(new DeleteBucketWebsiteRequest { BucketName = bucket });
        var (code, message) = await WebsiteError(ctx, bucket);
        Ensure(code == "NoSuchWebsiteConfiguration", $"expected NoSuchWebsiteConfiguration, got {Py.Repr(code)}");
        Ensure(message == NoWebsiteMessage, message ?? "None");
    }

    private const string NoWebsiteMessage = "The specified bucket does not have a website configuration";

    private static Task PutIndexAndError(AmazonS3Client s3, string bucket) =>
        s3.PutBucketWebsiteAsync(new PutBucketWebsiteRequest
        {
            BucketName = bucket,
            WebsiteConfiguration = new WebsiteConfiguration { IndexDocumentSuffix = "index.html", ErrorDocument = "error.html" },
        });

    private static async Task<WebsiteConfiguration> GetWebsite(AmazonS3Client s3, string bucket) =>
        (await s3.GetBucketWebsiteAsync(new GetBucketWebsiteRequest { BucketName = bucket })).WebsiteConfiguration;

    /// <summary>
    /// <c>_error(lambda: s3.get_bucket_website(Bucket=bucket))</c>. The .NET SDK answers GetBucketWebsite's
    /// 404 as an empty success, so the refusal is read off the wire.
    /// </summary>
    private static async Task<(string? Code, string? Message)> WebsiteError(FidelityContext ctx, string bucket)
    {
        var error = await S3Fidelity.RawError(ctx, "GET", $"{bucket}?website");
        return (error.Code, error.Message);
    }
}
