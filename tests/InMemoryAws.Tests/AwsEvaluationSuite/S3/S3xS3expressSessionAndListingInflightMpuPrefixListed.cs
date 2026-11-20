// Translated from behavioral/s3/check_s3x_s3express_session_and_listing_inflight_mpu_prefix_listed.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3x-s3express-session-and-listing-inflight-mpu-prefix-listed</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3xS3expressSessionAndListingInflightMpuPrefixListed
{
    private const string Check = "s3x-s3express-session-and-listing-inflight-mpu-prefix-listed";

    /// <summary>
    /// With an MPU open on <c>inflight/big.bin</c>, a directory bucket lists <c>inflight/</c> as a
    /// CommonPrefix while a general-purpose twin does not; neither lists the key, and abort clears it.
    /// </summary>
    [Test]
    [DisplayName("s3.s3x-s3express-session-and-listing-inflight-mpu-prefix-listed")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var directory = await S3Fidelity.DirectoryBucket(ctx, s3, "sim2real-s3x-mpu-");
        var twin = await S3Fidelity.GeneralPurposeBucket(ctx, s3, "sim2real-s3x-mpu-gp-", Check);
        const string settled = "settled/a.txt";
        const string inflight = "inflight/big.bin";
        foreach (var bucket in new[] { directory, twin })
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = settled, ContentBody = "settled" });
            var baseline = await CommonPrefixes(s3, bucket);
            Ensure(baseline.SequenceEqual(["settled/"]), $"{bucket}: baseline prefixes {Py.Repr(baseline)}, expected ['settled/']");
        }

        var uploads = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var bucket in new[] { directory, twin })
        {
            var up = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = inflight })).UploadId;
            ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, inflight, up));
            await s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = bucket,
                Key = inflight,
                UploadId = up,
                PartNumber = 1,
                InputStream = new MemoryStream(S3Fidelity.Repeat('p', 1024)),
            });
            uploads[bucket] = up;
        }

        // The same instant, both bucket types, identical key layout and identical open upload.
        var dirPrefixes = await CommonPrefixes(s3, directory);
        var twinPrefixes = await CommonPrefixes(s3, twin);
        Ensure(
            dirPrefixes.SequenceEqual(["inflight/", "settled/"]),
            $"directory bucket with an open MPU listed prefixes {Py.Repr(dirPrefixes)}, expected ['inflight/', 'settled/']");
        Ensure(
            twinPrefixes.SequenceEqual(["settled/"]),
            $"general-purpose twin with an open MPU listed prefixes {Py.Repr(twinPrefixes)}, expected ['settled/']");
        foreach (var bucket in new[] { directory, twin })
        {
            var keys = (await S3CoreFidelity.ListAllKeys(s3, bucket)).Order(StringComparer.Ordinal).ToList();
            Ensure(keys.SequenceEqual([settled]), $"{bucket}: the in-progress object surfaced as a key: {Py.Repr(keys)}");
        }

        var twinOpen = ((await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = twin, Prefix = "inflight/" })).MultipartUploads ?? [])
            .Select(u => u.UploadId).ToList();
        Ensure(twinOpen.SequenceEqual([uploads[twin]]), $"twin's MPU is not open as expected: {Py.Repr(twinOpen)}");

        // Transition: abort the directory bucket's upload and its prefix goes with it.
        await s3.AbortMultipartUploadAsync(directory, inflight, uploads[directory]);
        var after = await CommonPrefixes(s3, directory);
        Ensure(after.SequenceEqual(["settled/"]), $"after AbortMultipartUpload the directory bucket still lists {Py.Repr(after)}");
    }

    private static async Task<List<string>> CommonPrefixes(IAmazonS3 s3, string bucket)
    {
        var prefixes = new List<string>();
        string? token = null;
        do
        {
            var page = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Delimiter = "/", ContinuationToken = token });
            prefixes.AddRange(page.CommonPrefixes ?? []);
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (token is not null);
        return [.. prefixes.Order(StringComparer.Ordinal)];
    }
}
