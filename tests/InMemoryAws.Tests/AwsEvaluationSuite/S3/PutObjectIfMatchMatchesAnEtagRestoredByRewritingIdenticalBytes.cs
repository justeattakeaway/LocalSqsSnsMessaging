// Translated from behavioral/s3/check_put_object_if_match_matches_an_etag_restored_by_rewriting_identical_bytes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-object-if-match-matches-an-etag-restored-by-rewriting-identical-bytes</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutObjectIfMatchMatchesAnEtagRestoredByRewritingIdenticalBytes
{
    private const string Check = "put-object-if-match-matches-an-etag-restored-by-rewriting-identical-bytes";

    private static readonly (string?, string?, int) Precondition = ("PreconditionFailed", "At least one of the pre-conditions you specified did not hold", 412);

    /// <summary>
    /// If-Match compares ETags, not write history: after A, B, then A's bytes again, the first
    /// write's ETag matches and its PutObject lands, while B's ETag is refused 412 PreconditionFailed
    /// and the object keeps the bytes of the accepted write.
    /// Reference: https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-writes.html
    /// </summary>
    [Test]
    [DisplayName("s3.put-object-if-match-matches-an-etag-restored-by-rewriting-identical-bytes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check);
        const string key = "documents/ledger.json";
        // One attempt per conditional write: a retried PUT whose first try landed would read as stale.
        var once = ctx.S3Client(maxAttempts: 1);

        byte[] Token() => RandomNumberGenerator.GetBytes(64 + RandomNumberGenerator.GetInt32(64));
        var (a, b, c) = (Token(), Token(), Token());
        var first = (await Put(s3, bucket, key, a)).ETag;
        var second = (await Put(s3, bucket, key, b)).ETag;
        var third = (await Put(s3, bucket, key, a)).ETag;
        Ensure(
            (first, second, third) == (Md5(a), Md5(b), Md5(a)),
            $"PutObject ETags ({first}, {second}, {third}), expected the MD5s ({Md5(a)}, {Md5(b)}, {Md5(a)})");

        // The first write's ETag is current again, because the current bytes are A's.
        var put = await Put(once, bucket, key, c, first);
        Ensure(put.ETag == Md5(c), $"If-Match PutObject ETag {Py.Repr(put.ETag)}, expected {Py.Repr(Md5(c))}");
        var (data, seen) = await Read(s3, bucket, key);
        var want = (c.Length, Md5(c), Md5(c));
        Ensure(data.AsSpan().SequenceEqual(c) && seen == want, $"the accepted If-Match write: read (len, md5, ETag) {seen}, expected {want}");

        // B's ETag no longer matches: refused, and C survives.
        foreach (var (stale, label) in new[] { (second, "B"), (first, "A") })
        {
            var error = await FidelityContext.CaptureError(() => Put(once, bucket, key, Encoding.ASCII.GetBytes("lost"), stale))
                ?? throw Fail("the call SUCCEEDED where real AWS refuses it");
            var late = (error.Code, error.Message, (int)error.StatusCode);
            Ensure(late == Precondition, $"If-Match on {label}'s ETag: expected {Precondition}, got {late}");
        }

        var head = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(
            (head.ETag, head.ContentLength) == (Md5(c), c.Length),
            $"after the refusals HeadObject reports ({head.ETag}, {head.ContentLength}), expected ({Md5(c)}, {c.Length})");
        (data, seen) = await Read(s3, bucket, key);
        Ensure(data.AsSpan().SequenceEqual(c), $"a refused If-Match write replaced the object: read (len, md5, ETag) {seen}, expected {want}");
    }

    private static string Md5(byte[] body) => S3Fidelity.Md5ETag(body);

    /// <summary>(length, MD5 ETag of the bytes, reported ETag) of GetObject, for expected-vs-actual messages.</summary>
    private static async Task<(byte[] Data, (int, string, string) Seen)> Read(IAmazonS3 s3, string bucket, string key)
    {
        using var got = await s3.GetObjectAsync(bucket, key);
        var data = await S3Fidelity.ReadBody(got);
        return (data, (data.Length, Md5(data), got.ETag));
    }

    private static Task<PutObjectResponse> Put(IAmazonS3 s3, string bucket, string key, byte[] body, string? ifMatch = null) =>
        s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body), IfMatch = ifMatch });
}
