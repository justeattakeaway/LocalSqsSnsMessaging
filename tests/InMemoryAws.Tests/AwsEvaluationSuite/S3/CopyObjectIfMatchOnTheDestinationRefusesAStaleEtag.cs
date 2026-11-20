// Translated from behavioral/s3/check_copy_object_if_match_on_the_destination_refuses_a_stale_etag.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.copy-object-if-match-on-the-destination-refuses-a-stale-etag</summary>
[Category("AwsEvaluationSuite")]
public sealed class CopyObjectIfMatchOnTheDestinationRefusesAStaleEtag
{
    private const string Check = "copy-object-if-match-on-the-destination-refuses-a-stale-etag";
    private static readonly (string? Code, string? Message, int Status) Precondition = ("PreconditionFailed", "At least one of the pre-conditions you specified did not hold", 412);

    /// <summary>
    /// CopyObject with If-Match on the destination is refused 412 PreconditionFailed when the
    /// destination's ETag is stale and leaves its bytes untouched; with the current destination ETag
    /// the copy lands and the destination reads the source's bytes and ETag.
    /// Reference: https://docs.aws.amazon.com/AmazonS3/latest/API/API_CopyObject.html
    /// </summary>
    [Test]
    [DisplayName("s3.copy-object-if-match-on-the-destination-refuses-a-stale-etag")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check);
        const string src = "staging/release.bin";
        const string dst = "live/release.bin";
        // One attempt per conditional write: a retried copy whose first try landed would read as stale.
        var once = ctx.S3Client(maxAttempts: 1);

        var source = RandomNumberGenerator.GetBytes(2048 + RandomNumberGenerator.GetInt32(2048));
        var old = RandomNumberGenerator.GetBytes(300);
        var current = RandomNumberGenerator.GetBytes(301);
        Ensure((await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = src, InputStream = new MemoryStream(source) })).ETag == Md5(source), "source ETag is not its MD5");
        var stale = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = dst, InputStream = new MemoryStream(old) })).ETag;
        var live = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = dst, InputStream = new MemoryStream(current) })).ETag;
        Ensure((stale, live) == (Md5(old), Md5(current)), $"destination ETags ({Py.Repr(stale)}, {Py.Repr(live)}) are not the MD5s");

        CopyObjectRequest CopyIfMatch(string etag) => new() { DestinationBucket = bucket, DestinationKey = dst, SourceBucket = bucket, SourceKey = src, IfMatch = etag };

        var late = await Refusal(() => once.CopyObjectAsync(CopyIfMatch(stale)));
        Ensure(late == Precondition, $"CopyObject If-Match on a stale destination ETag: expected {Precondition}, got {late}");
        var (data, seen) = await Read(s3, bucket, dst);
        var want = (current.Length, live, live);
        Ensure(
            data.AsSpan().SequenceEqual(current) && seen == want,
            $"after the refused copy the destination reads (len, md5, ETag) {seen}, expected {want}");

        var done = await once.CopyObjectAsync(CopyIfMatch(live));
        Ensure(done.ETag == Md5(source), $"CopyObject ETag {Py.Repr(done.ETag)}, expected the source MD5 {Py.Repr(Md5(source))}");
        (data, seen) = await Read(s3, bucket, dst);
        want = (source.Length, Md5(source), Md5(source));
        Ensure(
            data.AsSpan().SequenceEqual(source) && seen == want,
            $"after the accepted copy the destination reads (len, md5, ETag) {seen}, expected the source's {want}");
        // The pre-copy ETag is stale now too.
        var again = await Refusal(() => once.CopyObjectAsync(CopyIfMatch(live)));
        Ensure(again == Precondition, $"CopyObject If-Match on the replaced ETag: expected {Precondition}, got {again}");
    }

    /// <summary>(Error.Code, Error.Message, HTTP status) of the error the call raises; FAILS on success.</summary>
    private static async Task<(string? Code, string? Message, int Status)> Refusal(Func<Task> call)
    {
        var err = await S3Fidelity.ExpectError(call, "the call SUCCEEDED where real AWS refuses it");
        return (err.Code, err.Message, (int)err.StatusCode);
    }

    private static string Md5(byte[] body) => "\"" + S3Fidelity.Md5Hex(body) + "\"";

    /// <summary>(length, MD5 ETag of the bytes, reported ETag) of GetObject, for expected-vs-actual messages.</summary>
    private static async Task<(byte[] Data, (int, string, string) Seen)> Read(IAmazonS3 s3, string bucket, string key)
    {
        using var got = await s3.GetObjectAsync(bucket, key);
        var data = await S3Fidelity.ReadBody(got);
        return (data, (data.Length, Md5(data), got.ETag));
    }
}
