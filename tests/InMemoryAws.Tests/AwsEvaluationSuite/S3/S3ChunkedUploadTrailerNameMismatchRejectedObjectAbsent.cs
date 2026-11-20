// Translated from behavioral/s3/check_s3_chunked_upload_trailer_name_mismatch_rejected_object_absent.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-chunked-upload-trailer-name-mismatch-rejected-object-absent</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3ChunkedUploadTrailerNameMismatchRejectedObjectAbsent
{
    /// <summary>
    /// An aws-chunked STREAMING-UNSIGNED-PAYLOAD-TRAILER PUT whose trailer chunk carries a different header
    /// name than the one declared in x-amz-trailer is refused with MalformedTrailerError and stores
    /// nothing; declaring and sending the same name succeeds.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-chunked-upload-trailer-name-mismatch-rejected-object-absent")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);
        var payload = S3Fidelity.RepeatText("trailer-name-mismatch-payload-bytes-", 50);
        var sha256Value = Convert.ToBase64String(SHA256.HashData(payload));

        const string badKey = "chunked/trailer-name-mismatch.bin";
        var (status, body) = await S3Fidelity.PutUnsignedTrailer(ctx, bucket, badKey, payload, "x-amz-checksum-crc32", "x-amz-checksum-sha256", sha256Value);
        Ensure(
            status == 400,
            "a trailer chunk whose header name does not match x-amz-trailer must be refused 400, " +
            $"got {status}: {S3Fidelity.Head(body, 300)}");
        Ensure(
            S3Fidelity.XmlCodeMessage(body) == ("MalformedTrailerError",
                "The request contained trailing data that was not well-formed or did not conform to " +
                "our published schema."),
            $"the name-mismatch refusal carries no MalformedTrailerError: {S3Fidelity.Head(body, 300)}");
        var error = await S3Fidelity.Error(() => s3.GetObjectAsync(bucket, badKey));
        Ensure(error.Code == "NoSuchKey", $"{Py.Repr(badKey)} must not exist after the rejected PUT, got {Py.Repr(error.Code)}");
        Ensure(error.Message == "The specified key does not exist.", $"get_object of the absent key must say 'The specified key does not exist.', got {Py.Repr(error.Message)}");

        var crc32Value = S3Fidelity.Crc32B64(payload);
        const string goodKey = "chunked/trailer-name-matches-control.bin";
        (status, body) = await S3Fidelity.PutUnsignedTrailer(ctx, bucket, goodKey, payload, "x-amz-checksum-crc32", "x-amz-checksum-crc32", crc32Value);
        Ensure(
            status == 200,
            "a trailer chunk whose name matches the declared x-amz-trailer must succeed, got " +
            $"{status}: {S3Fidelity.Head(body, 300)}");
        var head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = goodKey, ChecksumMode = ChecksumMode.ENABLED });
        Ensure(head.ChecksumCRC32 == crc32Value, $"the control object's ChecksumCRC32 is {Py.Repr(head.ChecksumCRC32)}, expected {Py.Repr(crc32Value)}");
    }
}
