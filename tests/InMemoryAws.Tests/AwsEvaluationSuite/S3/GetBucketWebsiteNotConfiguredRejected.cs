// Translated from behavioral/s3/check_get_bucket_website_not_configured_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.get-bucket-website-not-configured-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetBucketWebsiteNotConfiguredRejected
{
    /// <summary>
    /// GetBucketWebsite on a bucket with no website config fails with NoSuchWebsiteConfiguration;
    /// after PutBucketWebsite it reflects the config, and after DeleteBucketWebsite it fails again.
    /// </summary>
    [Test]
    [DisplayName("s3.get-bucket-website-not-configured-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var err = await WebsiteError(ctx, bucket);
        Ensure(err.Code == "NoSuchWebsiteConfiguration", $"expected NoSuchWebsiteConfiguration, got {S3Fidelity.Repr(err)}");
        Ensure(err.Message == NoWebsiteMessage, err.Message ?? "None");

        // Positive control: once a real config exists, the identical read succeeds and reflects it.
        await PutIndexAndError(s3, bucket);
        var got = await GetWebsite(s3, bucket);
        Ensure(got.IndexDocumentSuffix == "index.html", $"index doc not reflected: {Py.Repr(got.IndexDocumentSuffix)}");
        Ensure(got.ErrorDocument == "error.html", $"error doc not reflected: {Py.Repr(got.ErrorDocument)}");

        // Break it again: deleting the config must restore the original rejection, not a stale success.
        await s3.DeleteBucketWebsiteAsync(new DeleteBucketWebsiteRequest { BucketName = bucket });
        var errAfter = await WebsiteError(ctx, bucket);
        Ensure(errAfter.Code == "NoSuchWebsiteConfiguration", $"expected NoSuchWebsiteConfiguration after delete, got {S3Fidelity.Repr(errAfter)}");
        Ensure(errAfter.Message == NoWebsiteMessage, errAfter.Message ?? "None");
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
