// Translated from behavioral/s3/check_object_put_get_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-put-get-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectPutGetDelete
{
    private static readonly (string, string) NoSuchKey = ("NoSuchKey", "The specified key does not exist.");
    private static readonly (string, string) NoSuchBucket = ("NoSuchBucket", "The specified bucket does not exist");

    /// <summary>Bounded convergence for a deleted bucket to answer HeadBucket 404.</summary>
    private const double GoneS = 120.0;

    /// <summary>
    /// Random object bytes round-trip with the MD5 ETag on every read; the deleted object, then the
    /// deleted bucket, read not-found, are unlisted and refuse further use.
    /// </summary>
    [Test]
    [DisplayName("s3.object-put-get-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "lifecycle/test-object.bin";
        var body = RandomNumberGenerator.GetBytes(4096);
        // A single-part PUT under default (SSE-S3) encryption has the quoted hex MD5 of the body as
        // its ETag; every read of the object reports that same ETag.
        var expectedEtag = "\"" + Convert.ToHexStringLower(MD5.HashData(body)) + "\"";
        var putEtag = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body) })).ETag;
        Ensure(putEtag == expectedEtag, $"PutObject ETag {Py.Repr(putEtag)} != quoted MD5 {Py.Repr(expectedEtag)}");
        var headEtag = (await s3.GetObjectMetadataAsync(bucket, key)).ETag;
        Ensure(headEtag == expectedEtag, $"HeadObject ETag {Py.Repr(headEtag)} != quoted MD5 {Py.Repr(expectedEtag)}");
        using (var gotObj = await s3.GetObjectAsync(bucket, key))
        {
            var got = await S3Fidelity.ReadBody(gotObj);
            Ensure(got.AsSpan().SequenceEqual(body), $"4 KiB body did not round-trip byte-exact ({got.Length} bytes back)");
            Ensure(gotObj.ETag == expectedEtag, $"GetObject ETag {Py.Repr(gotObj.ETag)} != quoted MD5 {Py.Repr(expectedEtag)}");
        }

        await s3.DeleteObjectAsync(bucket, key);
        var refusal = Refusal(await Err(() => s3.GetObjectAsync(bucket, key)), "GetObject after delete", 404, exact: true);
        Ensure(refusal == NoSuchKey, $"GetObject after delete: expected {Py.Repr(NoSuchKey)}, got {Py.Repr(refusal)}");
        var listed = (await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = key })).S3Objects ?? [];
        Ensure(listed.Count == 0, $"deleted key still listed by ListObjectsV2: {Py.Repr(listed.Select(o => o.Key).ToList())}");

        refusal = Refusal(
            await Err(() => s3.CopyObjectAsync(new CopyObjectRequest { SourceBucket = bucket, SourceKey = key, DestinationBucket = bucket, DestinationKey = "lifecycle/copy-dest.txt" })),
            "CopyObject from deleted key",
            404);
        Ensure(refusal == NoSuchKey, $"CopyObject from deleted key: expected {Py.Repr(NoSuchKey)}, got {Py.Repr(refusal)}");

        const string reusedBody = "hello-sim2real-reused";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = reusedBody });
        var headLen = (await s3.GetObjectMetadataAsync(bucket, key)).ContentLength;
        Ensure(headLen == reusedBody.Length, $"HeadObject ContentLength {headLen} != {reusedBody.Length}");
        var gotReused = System.Text.Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket, key));
        Ensure(gotReused == reusedBody, $"key not reusable after delete: {Py.Repr(gotReused)} != {Py.Repr(reusedBody)}");

        await s3.DeleteObjectAsync(bucket, key);
        await s3.DeleteBucketAsync(bucket);
        // Bucket deletion converges: poll HeadBucket (bounded) until it answers 404, then grade it.
        // A HEAD refusal has no body: the SDK reads the status as the code and its reason as the message.
        refusal = Refusal(await PollHeadBucketGone(s3, bucket), "HeadBucket after DeleteBucket", 404, exact: true);
        Ensure(refusal == ("404", "Not Found"), $"HeadBucket after DeleteBucket: expected ('404', 'Not Found'), got {Py.Repr(refusal)}");
        var names = (await S3CoreFidelity.ListBucketNames(s3, bucket)).ToList();
        Ensure(!names.Contains(bucket), $"deleted bucket still listed by ListBuckets: {Py.Repr(names)}");
        refusal = Refusal(await Err(() => s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "x" })), "PutObject into deleted bucket", 404);
        Ensure(refusal == NoSuchBucket, $"PutObject into deleted bucket: expected {Py.Repr(NoSuchBucket)}, got {Py.Repr(refusal)}");
    }

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
