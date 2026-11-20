// Translated from behavioral/s3/check_s3_chunked_upload_trailer_checksum_stores_object_and_checksum.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-chunked-upload-trailer-checksum-stores-object-and-checksum</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3ChunkedUploadTrailerChecksumStoresObjectAndChecksum
{
    /// <summary>
    /// An aws-chunked PUT signed STREAMING-UNSIGNED-PAYLOAD-TRAILER with an x-amz-checksum-crc32 trailer
    /// stores the object bytes and records that trailer's checksum as object metadata.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-chunked-upload-trailer-checksum-stores-object-and-checksum")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);
        const string key = "chunked/trailer-ok.bin";
        var payload = S3Fidelity.RepeatText("aws-chunked-trailer-checksum-payload-", 60);
        var crc32 = S3Fidelity.Crc32B64(payload);
        var (status, body) = await S3Fidelity.PutUnsignedTrailer(ctx, bucket, key, payload, "x-amz-checksum-crc32", "x-amz-checksum-crc32", crc32);
        Ensure(status == 200, $"a valid aws-chunked trailer-checksum PUT returned {status}: {S3Fidelity.Head(body, 300)}");

        var head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key, ChecksumMode = ChecksumMode.ENABLED });
        Ensure(head.ChecksumCRC32 == crc32, $"HeadObject reports ChecksumCRC32={Py.Repr(head.ChecksumCRC32)}, expected {Py.Repr(crc32)}");
        using var got = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, ChecksumMode = ChecksumMode.ENABLED });
        var gotBody = await S3Fidelity.ReadBody(got);
        Ensure(gotBody.SequenceEqual(payload), $"stored body is {S3Fidelity.Head(gotBody)}, expected {S3Fidelity.Head(payload)}");
        Ensure(got.ChecksumCRC32 == crc32, $"GetObject reports ChecksumCRC32={Py.Repr(got.ChecksumCRC32)}, expected {Py.Repr(crc32)}");
    }
}
