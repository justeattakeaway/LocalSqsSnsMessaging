// Translated from behavioral/s3/check_empty_and_odd_sized_objects_under_unicode_keys_round_trip_with_md5_etags.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.empty-and-odd-sized-objects-under-unicode-keys-round-trip-with-md5-etags</summary>
[Category("AwsEvaluationSuite")]
public sealed class EmptyAndOddSizedObjectsUnderUnicodeKeysRoundTripWithMd5Etags
{
    private static readonly (string, string) NoSuchKey = ("NoSuchKey", "The specified key does not exist.");
    private static readonly (string, string) NoSuchBucket = ("NoSuchBucket", "The specified bucket does not exist");

    /// <summary>
    /// Bounded convergence for a deleted bucket to answer HeadBucket 404; a deadline, never an
    /// assertion on elapsed time.
    /// </summary>
    private const double GoneS = 40.0;

    /// <summary>
    /// A zero-byte object and an object of random odd length, under multi-byte UTF-8 keys with
    /// spaces, each round-trip byte-exact with their own MD5 ETag and size on every read and in the
    /// listing; once deleted each reads NoSuchKey, and the deleted bucket refuses further writes.
    /// </summary>
    [Test]
    [DisplayName("s3.empty-and-odd-sized-objects-under-unicode-keys-round-trip-with-md5-etags")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string emptyKey = "données/空 ファイル (0).txt";
        const string oddKey = "données/Ünïcødé — größe.bin";
        var oddLen = (2 * RandomNumberGenerator.GetInt32(40000)) + 1001;
        var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [emptyKey] = [], [oddKey] = RandomNumberGenerator.GetBytes(oddLen) };

        foreach (var (key, body) in objects)
        {
            var want = QuotedMd5(body);
            var putEtag = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body) })).ETag;
            Ensure(putEtag == want, $"PutObject {body.Length} bytes: ETag {Py.Repr(putEtag)} != quoted MD5 {Py.Repr(want)}");
            var head = await s3.GetObjectMetadataAsync(bucket, key);
            Ensure(
                (head.ETag, head.ContentLength) == (want, body.Length),
                $"HeadObject {body.Length} bytes: (ETag, ContentLength) {Py.Repr((head.ETag, head.ContentLength))}"
                + $" != {Py.Repr((want, (long)body.Length))}");
            using var got = await s3.GetObjectAsync(bucket, key);
            var data = await S3Fidelity.ReadBody(got);
            Ensure(data.AsSpan().SequenceEqual(body), $"{body.Length}-byte body did not round-trip byte-exact ({data.Length} bytes back)");
            Ensure(got.ETag == want, $"GetObject ETag {Py.Repr(got.ETag)} != quoted MD5 {Py.Repr(want)}");
        }

        var listed = (await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = "données/" })).S3Objects ?? [];
        var gotListing = listed.Select(o => (o.Key, o.Size ?? -1, o.ETag)).ToList();
        // ListObjectsV2 returns keys in ascending UTF-8 binary order.
        var wantListing = objects.Keys
            .OrderBy(k => Encoding.UTF8.GetBytes(k), Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b)))
            .Select(k => (k, (long)objects[k].Length, QuotedMd5(objects[k])))
            .ToList();
        Ensure(gotListing.SequenceEqual(wantListing), $"ListObjectsV2 reports {Py.Repr(gotListing)}, expected {Py.Repr(wantListing)}");

        foreach (var key in objects.Keys)
        {
            await s3.DeleteObjectAsync(bucket, key);
            var refusal = Refusal(await Err(() => s3.GetObjectAsync(bucket, key)), "GetObject after delete", 404, exact: true);
            Ensure(refusal == NoSuchKey, $"GetObject after delete: expected {Py.Repr(NoSuchKey)}, got {Py.Repr(refusal)}");
        }

        var left = (await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = "données/" })).S3Objects ?? [];
        Ensure(left.Count == 0, $"deleted keys still listed by ListObjectsV2: {Py.Repr(left.Select(o => o.Key).ToList())}");

        await s3.DeleteBucketAsync(bucket);
        // A HEAD refusal has no body: the SDK reads the status as the code and its reason as the message.
        var gone = Refusal(await PollHeadBucketGone(s3, bucket), "HeadBucket after DeleteBucket", 404, exact: true);
        Ensure(gone == ("404", "Not Found"), $"HeadBucket after DeleteBucket: expected ('404', 'Not Found'), got {Py.Repr(gone)}");
        var put = Refusal(await Err(() => s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = emptyKey, ContentBody = string.Empty })), "PutObject into deleted bucket", 404);
        Ensure(put == NoSuchBucket, $"PutObject into deleted bucket: expected {Py.Repr(NoSuchBucket)}, got {Py.Repr(put)}");
    }

    private static string QuotedMd5(byte[] body) => "\"" + Convert.ToHexStringLower(MD5.HashData(body)) + "\"";

    /// <summary>
    /// Call fn(); return the ClientError response it raises. An unexpected success raises, so a
    /// target that accepts what AWS refuses cannot read as a code mismatch.
    /// </summary>
    private static async Task<AwsError> Err(Func<Task> fn) =>
        await FidelityContext.CaptureError(fn) ?? throw Fail("expected a ClientError, but the call SUCCEEDED");

    /// <summary>
    /// (code, message) of a refusal, after grading its HTTP status: the class, or the exact
    /// status (exact=True) only where it is the signal: HEAD, and GET of an object.
    /// </summary>
    private static (string? Code, string? Message) Refusal(AwsError resp, string what, int status, bool exact = false)
    {
        var gotStatus = (int)resp.StatusCode;
        if (exact)
        {
            Ensure(gotStatus == status, $"{what}: expected HTTP {status}, got {gotStatus}");
        }
        else
        {
            Ensure(gotStatus / 100 == status / 100, $"{what}: expected HTTP {status / 100}xx, got {gotStatus}");
        }

        return S3Fidelity.Botocore(resp);
    }

    /// <summary>The HeadBucket refusal once the delete converges; a success meanwhile is retried.</summary>
    private static async Task<AwsError> PollHeadBucketGone(IAmazonS3 s3, string bucket)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            if (await FidelityContext.CaptureError(() => s3.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket })) is { } error)
            {
                return error;
            }

            if (clock.Elapsed.TotalSeconds >= GoneS)
            {
                throw Fail($"HeadBucket {bucket}: still answers {GoneS:0}s after DeleteBucket");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }
}
