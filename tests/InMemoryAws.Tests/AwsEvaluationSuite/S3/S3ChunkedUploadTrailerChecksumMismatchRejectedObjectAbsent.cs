// Translated from behavioral/s3/check_s3_chunked_upload_trailer_checksum_mismatch_rejected_object_absent.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-chunked-upload-trailer-checksum-mismatch-rejected-object-absent</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3ChunkedUploadTrailerChecksumMismatchRejectedObjectAbsent
{
    /// <summary>
    /// An aws-chunked STREAMING-UNSIGNED-PAYLOAD-TRAILER PUT whose trailer carries a CRC32 value that does
    /// not match the uploaded bytes is refused with BadDigest and stores nothing; the same upload with the
    /// correct trailer value succeeds.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-chunked-upload-trailer-checksum-mismatch-rejected-object-absent")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);
        var payload = S3Fidelity.RepeatText("trailer-checksum-mismatch-payload-bytes-", 50);
        var goodCrc32 = S3Fidelity.Crc32B64(payload);
        var wrong = S3Fidelity.Crc32(payload) ^ 0xFFFFFFFFu;
        var wrongCrc32 = Convert.ToBase64String([(byte)(wrong >> 24), (byte)(wrong >> 16), (byte)(wrong >> 8), (byte)wrong]);
        Ensure(wrongCrc32 != goodCrc32, "the corrupted CRC32 must differ from the correct one");

        const string badKey = "chunked/trailer-mismatch.bin";
        var (status, body) = await S3Fidelity.PutUnsignedTrailer(ctx, bucket, badKey, payload, "x-amz-checksum-crc32", "x-amz-checksum-crc32", wrongCrc32);
        Ensure(status == 400, $"a trailer CRC32 that does not match the payload must be refused 400, got {status}: {S3Fidelity.Head(body, 300)}");
        Ensure(
            S3Fidelity.XmlCodeMessage(body) == ("BadDigest", "The CRC32 you specified did not match the calculated checksum."),
            $"the mismatched-trailer refusal carries no BadDigest error: {S3Fidelity.Head(body, 300)}");
        var error = await S3Fidelity.Error(() => s3.GetObjectAsync(bucket, badKey));
        Ensure(error.Code == "NoSuchKey", $"{Py.Repr(badKey)} must not exist after the rejected PUT, got {Py.Repr(error.Code)}");
        Ensure(error.Message == "The specified key does not exist.", $"get_object of the absent key must say 'The specified key does not exist.', got {Py.Repr(error.Message)}");

        const string goodKey = "chunked/trailer-ok-control.bin";
        (status, body) = await S3Fidelity.PutUnsignedTrailer(ctx, bucket, goodKey, payload, "x-amz-checksum-crc32", "x-amz-checksum-crc32", goodCrc32);
        Ensure(status == 200, $"the same upload with the correct trailer checksum must succeed, got {status}: {S3Fidelity.Head(body, 300)}");
        var head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = goodKey, ChecksumMode = ChecksumMode.ENABLED });
        Ensure(head.ChecksumCRC32 == goodCrc32, $"the control object's ChecksumCRC32 is {Py.Repr(head.ChecksumCRC32)}, expected {Py.Repr(goodCrc32)}");
    }
}
