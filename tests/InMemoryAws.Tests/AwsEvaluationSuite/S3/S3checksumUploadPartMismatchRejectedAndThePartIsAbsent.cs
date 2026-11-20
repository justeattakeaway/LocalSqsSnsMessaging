// Translated from behavioral/s3/check_s3checksum_upload_part_mismatch_rejected_and_the_part_is_absent.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3checksum-upload-part-mismatch-rejected-and-the-part-is-absent</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3checksumUploadPartMismatchRejectedAndThePartIsAbsent
{
    /// <summary>The multipart minimum for every part but the last.</summary>
    private const int PartSize = 5 * 1024 * 1024;
    private const int TailSize = 2048;
    private const int NParts = 2;

    /// <summary>
    /// UploadPart with a well-formed but wrong ChecksumSHA256 is refused with 400 BadDigest and its exact
    /// message and the part is not listed; completion without it fails with InvalidPart, and re-uploading
    /// it with the true digest completes the object byte for byte.
    /// </summary>
    [Test]
    [DisplayName("s3.s3checksum-upload-part-mismatch-rejected-and-the-part-is-absent")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "part-verified.bin";

        byte[][] parts = [S3Fidelity.Repeat('A', PartSize), S3Fidelity.Repeat('B', TailSize)];
        var whole = parts.SelectMany(p => p).ToArray();
        Ensure(parts.Length == NParts, $"expected {NParts} parts, built {parts.Length}");

        var created = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key, ChecksumAlgorithm = ChecksumAlgorithm.SHA256 });
        var uploadId = created.UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, key, uploadId));

        Task<UploadPartResponse> Upload(int number, string checksum) => s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartNumber = number,
            InputStream = new MemoryStream(parts[number - 1]),
            ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
            ChecksumSHA256 = checksum,
        });

        // --- Part 1 goes up correctly, so the upload has real state to preserve.
        var wantP1 = S3Fidelity.Sha256B64(parts[0]);
        var p1 = await Upload(1, wantP1);
        Ensure(p1.ChecksumSHA256 == wantP1, $"UploadPart 1 reported {Py.Repr(p1.ChecksumSHA256)}, the client computed {Py.Repr(wantP1)}");

        // --- Break exactly one thing: part 2's checksum.
        var wantP2 = S3Fidelity.Sha256B64(parts[1]);
        var wrongP2 = S3Fidelity.Sha256B64([.. parts[1], (byte)'x']);
        Ensure(wrongP2 != wantP2, "the wrong part digest collided with the correct one");
        Ensure(Convert.FromBase64String(wrongP2).Length == 32, $"the wrong part digest must be a well-formed 32-byte SHA-256, got {Convert.FromBase64String(wrongP2).Length} bytes");
        var exc = await FidelityContext.CaptureError(() => Upload(2, wrongP2));
        Ensure(exc is not null, $"UploadPart accepted ChecksumSHA256={Py.Repr(wrongP2)} for a part whose true digest is {Py.Repr(wantP2)} — part checksums are being stored rather than verified");
        Ensure(exc!.Code == "BadDigest", $"expected BadDigest for a mismatched part checksum, got {Py.Repr(exc.Code)}");
        Ensure(exc.StatusCode == HttpStatusCode.BadRequest, $"expected HTTP 400 for BadDigest, got {(int)exc.StatusCode}");
        Ensure(exc.Message == "The SHA256 you specified did not match the calculated checksum.", $"the mismatched part checksum's refusal message is {Py.Repr(exc.Message)}");

        // --- Effect absent: the refused part was never registered against the upload.
        var registered = await S3Fidelity.ListedPartsSha256(s3, bucket, key, uploadId);
        Ensure(
            registered.SequenceEqual([(1, wantP1)]),
            $"ListParts reports {Py.Repr(registered.Select(r => r.ToString()).ToList())}; after the refusal only part 1 may be registered, so the expected list is [(1, {Py.Repr(wantP1)})]");

        // --- Effect absent, second reading: the object cannot be completed without part 2.
        var incomplete = await FidelityContext.CaptureError(() => s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags =
            [
                new PartETag { PartNumber = 1, ETag = p1.ETag, ChecksumSHA256 = wantP1 },
                new PartETag { PartNumber = 2, ETag = p1.ETag, ChecksumSHA256 = wantP2 },
            ],
        }));
        Ensure(incomplete is not null, "CompleteMultipartUpload assembled an object from a part that was never uploaded");
        Ensure(incomplete!.Code == "InvalidPart", $"expected InvalidPart when completing with a part that was refused at upload, got {Py.Repr(incomplete.Code)}");
        Ensure(
            incomplete.Message == "One or more of the specified parts could not be found.  The part may not have been uploaded, or the specified entity tag may not match the part's entity tag.",
            $"the incomplete-upload refusal's message is {Py.Repr(incomplete.Message)}");
        var absent = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, key));
        Ensure(absent is not null, "the failed completion created the object anyway");
        Ensure(absent!.StatusCode == HttpStatusCode.NotFound, $"HeadObject returned {(int)absent.StatusCode}, expected 404");

        // --- POSITIVE CONTROL: the identical chunk with its true digest completes the upload.
        var p2 = await Upload(2, wantP2);
        Ensure(p2.ChecksumSHA256 == wantP2, $"UploadPart 2 reported {Py.Repr(p2.ChecksumSHA256)}, the client computed {Py.Repr(wantP2)}");
        var after = await S3Fidelity.ListedPartsSha256(s3, bucket, key, uploadId);
        Ensure(
            after.SequenceEqual([(1, wantP1), (2, wantP2)]),
            $"ListParts reports {Py.Repr(after.Select(r => r.ToString()).ToList())} after the corrected upload, want [(1, {Py.Repr(wantP1)}), (2, {Py.Repr(wantP2)})]");

        var wantComposite = S3Fidelity.Sha256B64([.. parts.SelectMany(SHA256.HashData)]) + $"-{NParts}";
        var done = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags =
            [
                new PartETag { PartNumber = 1, ETag = p1.ETag, ChecksumSHA256 = wantP1 },
                new PartETag { PartNumber = 2, ETag = p2.ETag, ChecksumSHA256 = wantP2 },
            ],
        });
        Ensure(done.ChecksumSHA256 == wantComposite, $"the completion reported {Py.Repr(done.ChecksumSHA256)}; the composite of these two part digests is {Py.Repr(wantComposite)}");
        using var got = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, ChecksumMode = ChecksumMode.ENABLED });
        var body = await S3Fidelity.ReadBody(got);
        Ensure(
            body.AsSpan().SequenceEqual(whole),
            $"the assembled object is {body.Length} bytes, want {whole.Length}; the part that was refused and re-uploaded must appear exactly once, in order");
        Ensure(got.ChecksumSHA256 == wantComposite, $"GetObject reported {Py.Repr(got.ChecksumSHA256)}, want {Py.Repr(wantComposite)}");
    }
}
