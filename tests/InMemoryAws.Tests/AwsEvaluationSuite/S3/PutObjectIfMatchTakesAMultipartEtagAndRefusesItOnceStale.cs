// Translated from behavioral/s3/check_put_object_if_match_takes_a_multipart_etag_and_refuses_it_once_stale.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-object-if-match-takes-a-multipart-etag-and-refuses-it-once-stale</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutObjectIfMatchTakesAMultipartEtagAndRefusesItOnceStale
{
    private const string Check = "put-object-if-match-takes-a-multipart-etag-and-refuses-it-once-stale";
    private const int Part = 5 * 1024 * 1024;
    private static readonly (string?, string?, int) Precondition = ("PreconditionFailed", "At least one of the pre-conditions you specified did not hold", 412);

    /// <summary>
    /// PutObject If-Match accepts a multipart object's composite ETag and replaces it; the same
    /// ETag, stale after that write, is refused 412 PreconditionFailed and the newer bytes survive.
    /// Reference: https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-writes.html
    /// </summary>
    [Test]
    [DisplayName("s3.put-object-if-match-takes-a-multipart-etag-and-refuses-it-once-stale")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check, "sim2real-behav-bkt-");
        const string key = "locked/state.bin";

        byte[][] parts = [S3Fidelity.Repeat('a', Part), S3Fidelity.Repeat('b', 1024)];
        var completedEtag = await S3Fidelity.MultipartPut(ctx, s3, bucket, key, parts);
        var composite = S3Fidelity.MultipartETag(parts);
        Ensure(completedEtag == composite, $"CompleteMultipartUpload ETag {Py.Repr(completedEtag)}, expected the composite {Py.Repr(composite)}");
        var head = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(head.ETag == composite, $"HeadObject ETag {Py.Repr(head.ETag)}, expected {Py.Repr(composite)}");

        // One attempt per conditional write: a retried PUT whose first try landed would read as stale.
        var once = ctx.S3Client(maxAttempts: 1);

        // The writer holding the composite ETag wins: If-Match on it replaces the object.
        var first = Encoding.UTF8.GetBytes("written-under-the-composite-etag");
        var put = await once.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(first), IfMatch = composite });
        var firstEtag = S3Fidelity.Md5ETag(first);
        Ensure(put.ETag == firstEtag, $"PutObject ETag {Py.Repr(put.ETag)}, expected the MD5 {Py.Repr(firstEtag)}");
        using (var got = await s3.GetObjectAsync(bucket, key))
        {
            Ensure((await S3Fidelity.ReadBody(got)).AsSpan().SequenceEqual(first), "GetObject did not return the bytes the If-Match write stored");
            Ensure(got.ETag == firstEtag, $"GetObject ETag {Py.Repr(got.ETag)}, expected {Py.Repr(firstEtag)}");
        }

        // A second writer still holding the composite ETag is now stale: refused, nothing replaced.
        var lateError = await S3Fidelity.ExpectError(
            () => once.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "lost-update", IfMatch = composite }),
            "the call SUCCEEDED where real AWS refuses it");
        var late = (lateError.Code, lateError.Message, (int)lateError.StatusCode);
        Ensure(late == Precondition, $"a stale If-Match PutObject: expected {Precondition}, got {late}");
        using var after = await s3.GetObjectAsync(bucket, key);
        Ensure((await S3Fidelity.ReadBody(after)).AsSpan().SequenceEqual(first), "the refused stale write replaced the object");
        Ensure(after.ETag == firstEtag, $"after the refusal the ETag is {Py.Repr(after.ETag)}, expected {Py.Repr(firstEtag)}");
    }
}
