// Translated from behavioral/s3/check_s3_chunked_upload_missing_decoded_content_length_rejected_object_absent.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-chunked-upload-missing-decoded-content-length-rejected-object-absent</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3ChunkedUploadMissingDecodedContentLengthRejectedObjectAbsent
{
    /// <summary>
    /// An aws-chunked STREAMING-AWS4-HMAC-SHA256-PAYLOAD PUT that omits x-amz-decoded-content-length is
    /// refused with MissingContentLength and stores nothing; the same chunked upload carrying that header
    /// succeeds.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-chunked-upload-missing-decoded-content-length-rejected-object-absent")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);
        var payload = S3Fidelity.RepeatText("missing-decoded-content-length-payload-", 50);

        const string badKey = "chunked/no-decoded-length.bin";
        var (status, body) = await S3Fidelity.PutSignedChunked(ctx, bucket, badKey, payload, includeDecodedLength: false);
        Ensure(
            status == 411,
            "a chunked PUT with no x-amz-decoded-content-length must be refused 411, got " +
            $"{status}: {S3Fidelity.Head(body, 300)}");
        Ensure(
            S3Fidelity.XmlCodeMessage(body) == ("MissingContentLength", "You must provide the Content-Length HTTP header."),
            $"the missing-decoded-length refusal carries no MissingContentLength error: {S3Fidelity.Head(body, 300)}");
        var error = await S3Fidelity.Error(() => s3.GetObjectAsync(bucket, badKey));
        Ensure(error.Code == "NoSuchKey", $"{Py.Repr(badKey)} must not exist after the rejected PUT, got {Py.Repr(error.Code)}");
        Ensure(error.Message == "The specified key does not exist.", $"get_object of the absent key must say 'The specified key does not exist.', got {Py.Repr(error.Message)}");

        const string goodKey = "chunked/decoded-length-present-control.bin";
        (status, body) = await S3Fidelity.PutSignedChunked(ctx, bucket, goodKey, payload, includeDecodedLength: true);
        Ensure(
            status == 200,
            "the same chunked upload with x-amz-decoded-content-length must succeed, got " +
            $"{status}: {S3Fidelity.Head(body, 300)}");
        var got = await S3Fidelity.GetBytes(s3, bucket, goodKey);
        Ensure(got.SequenceEqual(payload), $"the control object's stored body is {S3Fidelity.Head(got)}, expected {S3Fidelity.Head(payload)}");
    }
}
