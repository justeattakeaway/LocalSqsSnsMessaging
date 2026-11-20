// Translated from behavioral/s3/check_bucket_subresource_deletes_leave_bucket_and_objects_intact.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-subresource-deletes-leave-bucket-and-objects-intact</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketSubresourceDeletesLeaveBucketAndObjectsIntact
{
    /// <summary>
    /// Deleting a bucket's CORS, website, metrics, tagging, replication or policy subresource answers
    /// 204 and removes only that subresource: each GET is refused with its exact NoSuch* error, a repeated
    /// CORS delete answers 204, a repeated metrics delete is NoSuchConfiguration, and the object survives.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-subresource-deletes-leave-bucket-and-objects-intact")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-subres-");
        var etag = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Key, InputStream = new MemoryStream(Body) })).ETag;
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration { Rules = [new CORSRule { AllowedMethods = ["GET"], AllowedOrigins = ["https://example.com"] }] },
        });
        await s3.PutBucketWebsiteAsync(new PutBucketWebsiteRequest { BucketName = bucket, WebsiteConfiguration = new WebsiteConfiguration { IndexDocumentSuffix = "index.html" } });
        await s3.PutBucketMetricsConfigurationAsync(new PutBucketMetricsConfigurationRequest
        {
            BucketName = bucket,
            MetricsId = MetricsId,
            MetricsConfiguration = new MetricsConfiguration { MetricsId = MetricsId },
        });
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = bucket, TagSet = [new Tag { Key = "sim2real", Value = "bucket-subresource-deletes" }] });

        // Positive control: every configured subresource is readable before any delete.
        var cors = (await s3.GetCORSConfigurationAsync(new GetCORSConfigurationRequest { BucketName = bucket })).Configuration.Rules;
        Ensure(cors.Count == 1 && cors[0].AllowedOrigins.SequenceEqual(["https://example.com"]), Py.Repr(cors.Select(r => r.AllowedOrigins).ToList()));
        var website = (await s3.GetBucketWebsiteAsync(new GetBucketWebsiteRequest { BucketName = bucket })).WebsiteConfiguration;
        Ensure(website.IndexDocumentSuffix == "index.html", Py.Repr(website.IndexDocumentSuffix));
        var metrics = await s3.GetBucketMetricsConfigurationAsync(new GetBucketMetricsConfigurationRequest { BucketName = bucket, MetricsId = MetricsId });
        Ensure(metrics.MetricsConfiguration.MetricsId == MetricsId, Py.Repr(metrics.MetricsConfiguration.MetricsId));
        var tagSet = (await s3.GetBucketTaggingAsync(new GetBucketTaggingRequest { BucketName = bucket })).TagSet;
        Ensure(tagSet.Count == 1 && tagSet[0].Key == "sim2real" && tagSet[0].Value == "bucket-subresource-deletes", Py.Repr(tagSet.Select(t => $"{t.Key}={t.Value}").ToList()));
        await AssertBucketAndObject(s3, bucket, etag, "configuring the subresources");

        (string Label, Func<Task<Amazon.Runtime.AmazonWebServiceResponse>> Delete)[] deletes =
        [
            ("DeleteBucketCors", async () => await s3.DeleteCORSConfigurationAsync(new DeleteCORSConfigurationRequest { BucketName = bucket })),
            ("DeleteBucketWebsite", async () => await s3.DeleteBucketWebsiteAsync(new DeleteBucketWebsiteRequest { BucketName = bucket })),
            ("DeleteBucketMetricsConfiguration", async () => await s3.DeleteBucketMetricsConfigurationAsync(new DeleteBucketMetricsConfigurationRequest { BucketName = bucket, MetricsId = MetricsId })),
            ("DeleteBucketTagging", async () => await s3.DeleteBucketTaggingAsync(new DeleteBucketTaggingRequest { BucketName = bucket })),
            ("DeleteBucketReplication on a bucket with none", async () => await s3.DeleteBucketReplicationAsync(new DeleteBucketReplicationRequest { BucketName = bucket })),
            ("DeleteBucketPolicy on a bucket with none", async () => await s3.DeleteBucketPolicyAsync(new DeleteBucketPolicyRequest { BucketName = bucket })),
        ];
        foreach (var (label, delete) in deletes)
        {
            var status = (int)(await delete()).HttpStatusCode;
            Ensure(status == 204, $"{label} answered {status}, expected 204");
            await AssertBucketAndObject(s3, bucket, etag, label);
        }

        // The .NET SDK answers GetBucketWebsite's, GetBucketMetricsConfiguration's, GetBucketReplication's
        // and GetBucketPolicy's 404 as an empty success; those refusals are read off the wire.
        (string Label, Func<Task<(string? Code, string? Message)>> Read, (string? Code, string? Message) Expected)[] gone =
        [
            ("GetBucketCors", () => Refused(() => s3.GetCORSConfigurationAsync(new GetCORSConfigurationRequest { BucketName = bucket })),
                ("NoSuchCORSConfiguration", "The CORS configuration does not exist")),
            ("GetBucketWebsite", () => RawRefused(ctx, $"{bucket}?website"),
                ("NoSuchWebsiteConfiguration", "The specified bucket does not have a website configuration")),
            ("GetBucketMetricsConfiguration", () => RawRefused(ctx, $"{bucket}?metrics&id={MetricsId}"),
                NoConfig),
            ("GetBucketTagging", () => Refused(() => s3.GetBucketTaggingAsync(new GetBucketTaggingRequest { BucketName = bucket })),
                ("NoSuchTagSet", "The TagSet does not exist")),
            ("GetBucketReplication", () => RawRefused(ctx, $"{bucket}?replication"),
                ("ReplicationConfigurationNotFoundError", "The replication configuration was not found")),
            ("GetBucketPolicy", () => RawRefused(ctx, $"{bucket}?policy"),
                ("NoSuchBucketPolicy", "The bucket policy does not exist")),
        ];
        foreach (var (label, read, expected) in gone)
        {
            var got = await RefusedEventually(read, label);
            Ensure(got == expected, $"{label} after its delete answered {S3Fidelity.Repr(got)}, expected {S3Fidelity.Repr(expected)}");
        }

        // A repeated CORS delete is idempotent; a repeated metrics delete names the missing id.
        var again = (int)(await s3.DeleteCORSConfigurationAsync(new DeleteCORSConfigurationRequest { BucketName = bucket })).HttpStatusCode;
        Ensure(again == 204, $"a second DeleteBucketCors answered {again}, expected 204");
        var err = await S3Fidelity.ClientError(
            () => s3.DeleteBucketMetricsConfigurationAsync(new DeleteBucketMetricsConfigurationRequest { BucketName = bucket, MetricsId = MetricsId }),
            "expected the call to be refused, but it succeeded");
        Ensure(err == NoConfig, $"a second DeleteBucketMetricsConfiguration answered {S3Fidelity.Repr(err)}, expected {S3Fidelity.Repr(NoConfig)}");
        var listed = await S3CoreFidelity.ListAllKeys(s3, bucket);
        Ensure(listed.SequenceEqual([Key]), $"after every subresource delete the bucket lists {Py.Repr(listed)}");
        await AssertBucketAndObject(s3, bucket, etag, "the repeated deletes");
    }

    private const string Key = "subresource-survivor.txt";
    private const string MetricsId = "sim2real-metrics";
    private static readonly byte[] Body = "subresource deletes must not touch me"u8.ToArray();
    private static readonly (string? Code, string? Message) NoConfig = ("NoSuchConfiguration", "The specified configuration does not exist.");

    private static async Task AssertBucketAndObject(AmazonS3Client s3, string bucket, string etag, string after)
    {
        var status = (int)(await s3.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket })).HttpStatusCode;
        Ensure(status == 200, $"HeadBucket answered {status} after {after}; the bucket must survive");
        using var got = await s3.GetObjectAsync(bucket, Key);
        var body = await S3Fidelity.ReadBody(got);
        Ensure(
            body.SequenceEqual(Body) && got.ETag == etag,
            $"after {after} the object reads {S3Fidelity.Head(body)} with ETag {Py.Repr(got.ETag)}; expected the original {S3Fidelity.Head(Body)} with ETag {Py.Repr(etag)}");
    }

    private static async Task<(string? Code, string? Message)> Refused(Func<Task> call) =>
        await FidelityContext.CaptureError(call) is { } error ? S3Fidelity.Botocore(error) : (null, null);

    private static async Task<(string? Code, string? Message)> RawRefused(FidelityContext ctx, string pathAndQuery)
    {
        var (status, code, message) = await S3Fidelity.Raw(ctx, "GET", pathAndQuery);
        return status < 300 ? (null, null) : (code, message);
    }

    /// <summary>_refused_eventually(fn, what): (Code, Message) once fn is refused; a config delete may reach its GET a little late.</summary>
    private static async Task<(string? Code, string? Message)> RefusedEventually(Func<Task<(string? Code, string? Message)>> read, string what, double budgetSeconds = 60.0)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var got = await read();
            if (got.Code is not null)
            {
                return got;
            }

            if (clock.Elapsed.TotalSeconds >= budgetSeconds)
            {
                throw Fail($"{what} still answered {budgetSeconds}s after its delete");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }
}
