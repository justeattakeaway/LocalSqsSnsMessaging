// Translated from behavioral/s3/check_s3_chunked_upload_wrong_chunk_signature_rejected_object_absent.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-chunked-upload-wrong-chunk-signature-rejected-object-absent</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3ChunkedUploadWrongChunkSignatureRejectedObjectAbsent
{
    /// <summary>
    /// An aws-chunked STREAMING-AWS4-HMAC-SHA256-PAYLOAD PUT whose first chunk-signature does not chain
    /// from the seed signature is refused with SignatureDoesNotMatch and stores nothing; the identical
    /// upload with the correct chunk signature succeeds.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-chunked-upload-wrong-chunk-signature-rejected-object-absent")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);
        var payload = S3Fidelity.RepeatText("wrong-chunk-signature-payload-bytes-", 50);

        const string badKey = "chunked/wrong-chunk-signature.bin";
        var (status, body) = await S3Fidelity.PutSignedChunked(ctx, bucket, badKey, payload, corruptFirstChunkSignature: true);
        Ensure(
            status == 403,
            "a chunked PUT whose chunk-signature does not chain correctly must be refused 403, " +
            $"got {status}: {S3Fidelity.Head(body, 300)}");
        var (code, message) = S3Fidelity.XmlCodeMessage(body);
        Ensure(
            code == "SignatureDoesNotMatch",
            $"the wrong-chunk-signature refusal carries code {Py.Repr(code)}, expected SignatureDoesNotMatch: {S3Fidelity.Head(body, 300)}");
        Ensure(message == S3Fidelity.SignatureMismatchMessage, $"unexpected SignatureDoesNotMatch message: {Py.Repr(message)}");
        var error = await S3Fidelity.Error(() => s3.GetObjectAsync(bucket, badKey));
        Ensure(error.Code == "NoSuchKey", $"{Py.Repr(badKey)} must not exist after the rejected PUT, got {Py.Repr(error.Code)}");
        Ensure(error.Message == "The specified key does not exist.", $"get_object of the absent key must say 'The specified key does not exist.', got {Py.Repr(error.Message)}");

        const string goodKey = "chunked/correct-chunk-signature-control.bin";
        (status, body) = await S3Fidelity.PutSignedChunked(ctx, bucket, goodKey, payload, corruptFirstChunkSignature: false);
        Ensure(
            status == 200,
            "the same chunked upload with its real chunk signature must succeed, got " +
            $"{status}: {S3Fidelity.Head(body, 300)}");
        var got = await S3Fidelity.GetBytes(s3, bucket, goodKey);
        Ensure(got.SequenceEqual(payload), $"the control object's stored body is {S3Fidelity.Head(got)}, expected {S3Fidelity.Head(payload)}");
    }
}
