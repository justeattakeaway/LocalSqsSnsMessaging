// Translated from behavioral/s3/check_multipart_upload_and_presigned_get_round_trip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Security.Cryptography;
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.multipart-upload-and-presigned-get-round-trip</summary>
[Category("AwsEvaluationSuite")]
public sealed class MultipartUploadAndPresignedGetRoundTrip
{
    /// <summary>
    /// A two-part upload completes with ETag md5(md5(p1)||md5(p2))-2 and reads back byte-exact through
    /// GetObject and a presigned GET; an aborted upload leaves no parts; delete is final.
    /// </summary>
    [Test]
    [DisplayName("s3.multipart-upload-and-presigned-get-round-trip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        const string key = "multipart.bin";
        const string abortedKey = "aborted.bin";
        var s3 = ctx.S3;
        var bucket = FidelityContext.UniqueName("sim2real-behav-mpu-");
        await s3.PutBucketAsync(new PutBucketRequest
        {
            BucketName = bucket,
            UseClientRegion = false,
            PutBucketConfiguration = new PutBucketConfiguration { LocationConstraint = new BucketLocationConstraint(ctx.Region) },
        });
        ctx.OnCleanup(() => S3Fidelity.EmptyAndDelete(s3, bucket));

        var part1 = S3Fidelity.RandomBytes(5 * 1024 * 1024);
        var part2 = S3Fidelity.RandomBytes(1024);
        byte[] whole = [.. part1, .. part2];
        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key })).UploadId;
        Ensure(!string.IsNullOrEmpty(uploadId), "CreateMultipartUpload returned an empty UploadId");
        var parts = new List<PartETag>();
        foreach (var (number, data) in new[] { (1, part1), (2, part2) })
        {
            var etag = (await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartNumber = number, InputStream = new MemoryStream(data) })).ETag;
            Ensure(etag == $"\"{S3Fidelity.Md5Hex(data)}\"", $"part {number} ETag {Py.Repr(etag)} is not the MD5 of its bytes");
            parts.Add(S3Fidelity.Part(number, etag));
        }

        var listed = (await s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId })).Parts ?? [];
        var sizes = listed.Select(p => (p.PartNumber, p.Size)).ToList();
        Ensure(
            sizes.SequenceEqual([(1, (long?)part1.Length), (2, part2.Length)]),
            $"ListParts before completion reports [{string.Join(", ", sizes)}]");
        var done = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartETags = parts });
        var composite = Convert.ToHexStringLower(MD5.HashData([.. S3Fidelity.Md5(part1), .. S3Fidelity.Md5(part2)]));
        var wantEtag = $"\"{composite}-2\"";
        Ensure(done.ETag == wantEtag, $"CompleteMultipartUpload ETag {Py.Repr(done.ETag)}, expected md5 of the part MD5s {Py.Repr(wantEtag)}");

        using (var got = await s3.GetObjectAsync(bucket, key))
        {
            var body = await S3Fidelity.ReadBody(got);
            Ensure(got.ETag == wantEtag, $"GetObject ETag {Py.Repr(got.ETag)}, expected {Py.Repr(wantEtag)}");
            Ensure(
                body.SequenceEqual(whole),
                $"GetObject returned {body.Length} bytes (md5 {S3Fidelity.Md5Hex(body)}), expected " +
                $"{whole.Length} bytes (md5 {S3Fidelity.Md5Hex(whole)})");
        }

        var url = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest { BucketName = bucket, Key = key, Verb = HttpVerb.GET, Expires = DateTime.UtcNow.AddSeconds(300) });
        var fetched = await Fetch(ctx, url);
        Ensure(
            fetched.SequenceEqual(whole),
            $"the presigned GET returned {fetched.Length} bytes " +
            $"(md5 {S3Fidelity.Md5Hex(fetched)}), expected the {whole.Length} uploaded bytes");
        var pending = (await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket })).MultipartUploads ?? [];
        Ensure(pending.Count == 0, $"ListMultipartUploads after completion still lists {pending.Count} upload(s)");

        var second = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = abortedKey })).UploadId;
        await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = abortedKey, UploadId = second, PartNumber = 1, InputStream = new MemoryStream(part2) });
        await s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = bucket, Key = abortedKey, UploadId = second });
        var listError = await FidelityContext.CaptureError(() => s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = abortedKey, UploadId = second }));
        var (code, msg, status) = (listError?.Code, listError?.Message, listError is null ? 200 : (int)listError.StatusCode);
        Ensure(
            code == "NoSuchUpload" && status == 404 && !string.IsNullOrEmpty(msg),
            $"ListParts on the aborted upload answered {Py.Repr(code)} HTTP {status} message {Py.Repr(msg)}, " +
            "expected NoSuchUpload, 404 and a message");
        Ensure(
            msg == "The specified upload does not exist. The upload ID may be invalid, or the upload may have been aborted or completed.",
            $"expected the exact NoSuchUpload message AWS returns, got {Py.Repr(msg)}");
        pending = (await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket })).MultipartUploads ?? [];
        Ensure(pending.Count == 0, $"ListMultipartUploads after the abort still lists {pending.Count} upload(s)");
        Ensure(await HeadStatus(s3, bucket, abortedKey) == 404, "the aborted upload created an object");

        await s3.DeleteObjectAsync(bucket, key);
        Ensure(await HeadStatus(s3, bucket, key) == 404, "the deleted object still answers HeadObject");
        var getError = await FidelityContext.CaptureError(() => s3.GetObjectAsync(bucket, key)) ?? throw Fail("GetObject on the deleted object succeeded");
        Ensure(getError.Code == "NoSuchKey", $"GetObject on the deleted object answered {Py.Repr(getError.Code)}");
        Ensure(getError.Message == "The specified key does not exist.", $"expected the exact NoSuchKey message AWS returns, got {Py.Repr(getError.Message)}");

        await s3.DeleteBucketAsync(bucket);
        var clock = FidelityTime.StartNew();
        while (await HeadBucketStatus(s3, bucket) != 404)
        {
            Ensure(clock.Elapsed < TimeSpan.FromSeconds(30), "the deleted bucket still answers HeadBucket");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>GET a presigned URL with no credentials; any failure to serve is a fidelity failure.</summary>
    private static async Task<byte[]> Fetch(FidelityContext ctx, string url)
    {
        var (status, _, data) = await S3Fidelity.Send(ctx, "GET", url);
        Ensure(status == 200, $"DATAPLANE_UNIMPLEMENTED: the presigned GET URL did not serve the object: HTTP Error {status}: {S3Fidelity.Head(data)}");
        return data;
    }

    private static async Task<int> HeadStatus(IAmazonS3 s3, string bucket, string key) =>
        await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, key)) is { } error ? (int)error.StatusCode : 200;

    private static async Task<int> HeadBucketStatus(IAmazonS3 s3, string bucket) =>
        await FidelityContext.CaptureError(() => s3.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket })) is { } error ? (int)error.StatusCode : 200;
}
