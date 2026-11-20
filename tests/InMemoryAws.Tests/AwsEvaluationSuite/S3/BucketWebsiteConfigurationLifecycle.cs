// Translated from behavioral/s3/check_bucket_website_configuration_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-website-configuration-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketWebsiteConfigurationLifecycle
{
    /// <summary>
    /// A fresh bucket has no website config; PutBucketWebsite -> GetBucketWebsite roundtrips
    /// index/error docs exactly (no stray RedirectAllRequestsTo); DeleteBucketWebsite clears the
    /// static-site configuration entirely.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-website-configuration-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-website-");
        var (codeBefore, messageBefore) = await WebsiteError(ctx, bucket);
        Ensure(codeBefore == "NoSuchWebsiteConfiguration", $"expected NoSuchWebsiteConfiguration on a fresh bucket, got {Py.Repr(codeBefore)}");
        Ensure(messageBefore == NoWebsiteMessage, messageBefore ?? "None");
        await PutIndexAndError(s3, bucket);
        var got = await GetWebsite(s3, bucket);
        Ensure(got.IndexDocumentSuffix == "index.html", $"expected IndexDocument {{'Suffix': 'index.html'}}, got {Py.Repr(got.IndexDocumentSuffix)}");
        Ensure(got.ErrorDocument == "error.html", $"expected ErrorDocument {{'Key': 'error.html'}}, got {Py.Repr(got.ErrorDocument)}");
        Ensure(got.RedirectAllRequestsTo is null, $"unexpected RedirectAllRequestsTo {Py.Repr(got.RedirectAllRequestsTo?.HostName)}");
        await s3.DeleteBucketWebsiteAsync(new DeleteBucketWebsiteRequest { BucketName = bucket });
        var (codeAfter, messageAfter) = await WebsiteError(ctx, bucket);
        Ensure(codeAfter == "NoSuchWebsiteConfiguration", $"expected NoSuchWebsiteConfiguration after delete, got {Py.Repr(codeAfter)}");
        Ensure(messageAfter == NoWebsiteMessage, messageAfter ?? "None");
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
