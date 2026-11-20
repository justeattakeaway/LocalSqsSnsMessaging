// Translated from behavioral/s3/check_s3_complete_multipart_with_a_wrong_full_object_checksum_is_rejected_and_no_object_exists.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-complete-multipart-with-a-wrong-full-object-checksum-is-rejected-and-no-object-exists</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3CompleteMultipartWithAWrongFullObjectChecksumIsRejectedAndNoObjectExists
{
    /// <summary>5 MiB — the minimum size for a non-final part.</summary>
    private const int PartA = 5 * 1024 * 1024;

    /// <summary>1 MiB — final part, may be smaller.</summary>
    private const int PartB = 1 * 1024 * 1024;

    private const int Total = PartA + PartB;

    /// <summary>
    /// A wrong whole-object CRC64NVME on CompleteMultipartUpload is a 400 BadDigest: no object
    /// exists, the upload id stays listed, and the same upload then completes with the right checksum.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-complete-multipart-with-a-wrong-full-object-checksum-is-rejected-and-no-object-exists")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "wrong-full-object-checksum.bin";

        var partA = S3Fidelity.RandomBytes(PartA);
        var partB = S3Fidelity.RandomBytes(PartB);
        byte[] whole = [.. partA, .. partB];
        var correct = S3Fidelity.Crc64NvmeB64(whole);
        // A syntactically valid, correctly-sized CRC64NVME of DIFFERENT bytes: the request is
        // well-formed, so only a server-side recomputation over the assembled object can refuse it.
        var wrong = S3Fidelity.Crc64NvmeB64(Encoding.UTF8.GetBytes("a deliberately different payload for the wrong digest"));
        Ensure(wrong != correct, "the wrong digest collided with the correct one; pick other bytes");

        var created = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            ChecksumAlgorithm = ChecksumAlgorithm.CRC64NVME,
            ChecksumType = ChecksumType.FULL_OBJECT,
        });
        var uploadId = created.UploadId;
        // Registered before any assertion: an orphaned multipart upload bills for its stored parts.
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, key, uploadId));

        var manifest = new List<PartETag>();
        foreach (var (number, chunk) in new[] { (1, partA), (2, partB) })
        {
            var uploaded = await s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = uploadId,
                PartNumber = number,
                InputStream = new MemoryStream(chunk),
                ChecksumAlgorithm = ChecksumAlgorithm.CRC64NVME,
            });
            Ensure(
                uploaded.ChecksumCRC64NVME == S3Fidelity.Crc64NvmeB64(chunk),
                $"UploadPart {number} reported CRC64NVME {Py.Repr(uploaded.ChecksumCRC64NVME)}, the client computed {Py.Repr(S3Fidelity.Crc64NvmeB64(chunk))} over the same {chunk.Length} bytes");
            manifest.Add(new PartETag { ETag = uploaded.ETag, PartNumber = number, ChecksumCRC64NVME = uploaded.ChecksumCRC64NVME });
        }

        var exc = await FidelityContext.CaptureError(() => s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = manifest,
            ChecksumType = ChecksumType.FULL_OBJECT,
            ChecksumCRC64NVME = wrong,
            MpuObjectSize = Total,
        }));
        Ensure(
            exc is not null,
            $"CompleteMultipartUpload accepted a whole-object CRC64NVME of {Py.Repr(wrong)} for an object whose actual checksum is {Py.Repr(correct)} — the supplied checksum is being stored rather than verified, which is the over-permissive failure this check exists to catch");
        Ensure(exc!.Code == "BadDigest", $"expected BadDigest for a mismatched whole-object checksum, got {Py.Repr(exc.Code)}");
        const string want = "The crc64nvme you specified did not match the calculated checksum.";
        Ensure(exc.Message == want, $"expected {Py.Repr(want)} for a mismatched whole-object checksum, got {Py.Repr(exc.Message)}");
        Ensure(exc.StatusCode == HttpStatusCode.BadRequest, $"expected HTTP 400 for BadDigest, got {(int)exc.StatusCode}");

        var missing = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, key));
        Ensure(missing is not null, "the rejected completion created an object at the key — the checksum failure did not prevent the assembly");
        Ensure(missing!.StatusCode == HttpStatusCode.NotFound, $"HeadObject after the refusal returned {(int)missing.StatusCode}, expected 404");
        var listedKeys = (await S3Fidelity.ListedObjects(s3, bucket)).Select(o => o.Key).ToList();
        Ensure(listedKeys.Count == 0, $"the bucket holds {Py.Repr(listedKeys)} after the refused completion; it must be empty");

        // A rejected completion is a no-op on the upload, not an implicit abort.
        var listedUploadIds = await UploadIds(s3, bucket);
        Ensure(
            listedUploadIds.Contains(uploadId),
            $"the upload id vanished from ListMultipartUploads after the BadDigest refusal; listed {Py.Repr(listedUploadIds.Select(u => u[..16]).ToList())}. Real AWS leaves it open and completable — an emulator that aborts it on a checksum failure destroys the customer's uploaded parts");

        // Positive control: same upload id and parts, so the checksum is the only variable.
        var completed = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = manifest,
            ChecksumType = ChecksumType.FULL_OBJECT,
            ChecksumCRC64NVME = correct,
            MpuObjectSize = Total,
        });
        Ensure(completed.ChecksumCRC64NVME == correct, $"the control completion reported {Py.Repr(completed.ChecksumCRC64NVME)}, want {Py.Repr(correct)}");
        Ensure(completed.ChecksumType == ChecksumType.FULL_OBJECT, $"the control completion reported ChecksumType {Py.Repr(completed.ChecksumType?.Value)}");
        using var got = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, ChecksumMode = ChecksumMode.ENABLED });
        var body = await S3Fidelity.ReadBody(got);
        Ensure(body.AsSpan().SequenceEqual(whole), $"the control object is {body.Length} bytes, want {Total} — the parts were not assembled in order");
        Ensure(got.ChecksumCRC64NVME == correct, $"GetObject reported {Py.Repr(got.ChecksumCRC64NVME)} for the control object, want {Py.Repr(correct)}");

        var after = await UploadIds(s3, bucket);
        Ensure(!after.Contains(uploadId), $"the upload id is still listed after a SUCCESSFUL completion: {Py.Repr(after.Select(u => u[..16]).ToList())}");
    }

    private static async Task<List<string>> UploadIds(IAmazonS3 s3, string bucket) =>
        [.. ((await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket })).MultipartUploads ?? []).Select(u => u.UploadId)];
}
