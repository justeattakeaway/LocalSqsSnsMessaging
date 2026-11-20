// Translated from behavioral/s3/check_put_object_if_match_refuses_a_composite_etag_replaced_by_another_multipart_upload.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-object-if-match-refuses-a-composite-etag-replaced-by-another-multipart-upload</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutObjectIfMatchRefusesACompositeEtagReplacedByAnotherMultipartUpload
{
    private const string Check = "put-object-if-match-refuses-a-composite-etag-replaced-by-another-multipart-upload";
    private const int Part = 5 * 1024 * 1024;
    private static readonly (string?, string?, int) Precondition = ("PreconditionFailed", "At least one of the pre-conditions you specified did not hold", 412);

    /// <summary>
    /// A two-part object replaced by a three-part upload of different bytes leaves its composite
    /// ETag stale: PutObject If-Match on it is refused 412 PreconditionFailed and the three-part object
    /// survives, while If-Match on the new '-3' composite ETag replaces it.
    /// Reference: https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-writes.html
    /// </summary>
    [Test]
    [DisplayName("s3.put-object-if-match-refuses-a-composite-etag-replaced-by-another-multipart-upload")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check, "sim2real-behav-bkt-");
        const string key = "locked/model.bin";
        // One attempt per conditional write: a retried PUT whose first try landed would read as stale.
        var once = ctx.S3Client(maxAttempts: 1);

        byte[][] first = [S3Fidelity.RandomBytes(Part), S3Fidelity.RandomBytes(1 + RandomNumberGenerator.GetInt32(4096))];
        byte[][] second = [S3Fidelity.RandomBytes(Part), S3Fidelity.RandomBytes(Part), S3Fidelity.RandomBytes(1 + RandomNumberGenerator.GetInt32(4096))];
        var old = await S3Fidelity.MultipartPut(ctx, s3, bucket, key, first);
        Ensure(old == S3Fidelity.MultipartETag(first), $"first upload ETag {Py.Repr(old)}, expected {Py.Repr(S3Fidelity.MultipartETag(first))}");
        var @new = await S3Fidelity.MultipartPut(ctx, s3, bucket, key, second);
        Ensure(@new == S3Fidelity.MultipartETag(second), $"second upload ETag {Py.Repr(@new)}, expected {Py.Repr(S3Fidelity.MultipartETag(second))}");

        var lateError = await S3Fidelity.ExpectError(
            () => once.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "lost-update", IfMatch = old }),
            "the call SUCCEEDED where real AWS refuses it");
        var late = (lateError.Code, lateError.Message, (int)lateError.StatusCode);
        Ensure(late == Precondition, $"If-Match on the replaced composite ETag: expected {Precondition}, got {late}");
        var head = await s3.GetObjectMetadataAsync(bucket, key);
        var whole = second.SelectMany(p => p).ToArray();
        Ensure(
            head.ETag == @new && head.ContentLength == whole.Length,
            $"after the refusal HeadObject reports ({Py.Repr(head.ETag)}, {head.ContentLength}), expected ({Py.Repr(@new)}, {whole.Length})");

        var final = RandomNumberGenerator.GetBytes(777);
        var put = await once.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(final), IfMatch = @new });
        var finalEtag = S3Fidelity.Md5ETag(final);
        Ensure(put.ETag == finalEtag, $"If-Match PutObject ETag {Py.Repr(put.ETag)}, expected {Py.Repr(finalEtag)}");
        using var got = await s3.GetObjectAsync(bucket, key);
        Ensure(
            (await S3Fidelity.ReadBody(got)).AsSpan().SequenceEqual(final) && got.ETag == finalEtag,
            "the If-Match write on the current composite ETag did not replace the object");
    }
}
