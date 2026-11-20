// Translated from behavioral/s3/check_delete_objects_that_misses_one_key_leaves_delete_bucket_refused.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.delete-objects-that-misses-one-key-leaves-delete-bucket-refused</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteObjectsThatMissesOneKeyLeavesDeleteBucketRefused
{
    private static readonly (string, string) NotEmpty = ("BucketNotEmpty", "The bucket you tried to delete is not empty");
    private static readonly (string, string) NoSuchBucket = ("NoSuchBucket", "The specified bucket does not exist");

    /// <summary>
    /// A DeleteObjects batch that names every key but one empties nothing else away: DeleteBucket
    /// is still refused BucketNotEmpty and the one survivor reads back its bytes; deleting that last
    /// key is exactly what lets the identical DeleteBucket succeed.
    /// </summary>
    [Test]
    [DisplayName("s3.delete-objects-that-misses-one-key-leaves-delete-bucket-refused")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var count = 3 + RandomNumberGenerator.GetInt32(4);
        var bodies = Enumerable.Range(0, count).ToDictionary(
            i => $"batch/{i:00}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}.dat",
            i => RandomNumberGenerator.GetBytes(16 + i),
            StringComparer.Ordinal);
        foreach (var (key, body) in bodies)
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body) });
        }

        var sorted = bodies.Keys.Order(StringComparer.Ordinal).ToList();
        var survivor = sorted[RandomNumberGenerator.GetInt32(count)];
        var batch = sorted.Where(k => k != survivor).ToList();
        var resp = await s3.DeleteObjectsAsync(new DeleteObjectsRequest
        {
            BucketName = bucket,
            Objects = [.. batch.Select(k => new KeyVersion { Key = k })],
        });
        var deleted = (resp.DeletedObjects ?? []).Select(d => d.Key).Order(StringComparer.Ordinal).ToList();
        Ensure(
            deleted.SequenceEqual(batch) && (resp.DeleteErrors ?? []).Count == 0,
            $"DeleteObjects of {batch.Count} keys reported Deleted {Py.Repr(deleted)} Errors {(resp.DeleteErrors ?? []).Count}");
        var keys = await Keys(s3, bucket);
        Ensure(keys.SequenceEqual([survivor]), $"after the batch the bucket lists {Py.Repr(keys)}");

        var (code, msg, status) = CodeMsgStatus(await Err(() => s3.DeleteBucketAsync(bucket)));
        Ensure(
            (code, msg) == NotEmpty && status == 409,
            $"DeleteBucket with one key left: expected {NotEmpty} HTTP 409, got {(code, msg, status)}");
        var kept = await S3Fidelity.GetBytes(s3, bucket, survivor);
        Ensure(kept.AsSpan().SequenceEqual(bodies[survivor]), "the survivor's bytes changed across the refused DeleteBucket");

        resp = await s3.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = bucket, Objects = [new KeyVersion { Key = survivor }] });
        var survivorDeleted = (resp.DeletedObjects ?? []).Select(d => d.Key).ToList();
        Ensure(survivorDeleted.SequenceEqual([survivor]), $"DeleteObjects of the survivor: {Py.Repr(survivorDeleted)}");
        await s3.DeleteBucketAsync(bucket);

        var deadline = FidelityTime.StartNew();
        AwsError head;
        while (true)
        {
            if (await FidelityContext.CaptureError(() => s3.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket })) is { } err)
            {
                head = err;
                break;
            }

            Ensure(deadline.Elapsed < TimeSpan.FromSeconds(40), "the deleted bucket still answers HeadBucket");
            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(S3CoreFidelity.IsBareHead404(head), $"HeadBucket after delete: {CodeMsgStatus(head)}");
        (code, msg, status) = CodeMsgStatus(await Err(() => s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = survivor,
            InputStream = new MemoryStream(Encoding.UTF8.GetBytes("x")),
        })));
        Ensure(
            (code, msg) == NoSuchBucket && status == 404,
            $"PutObject into the deleted bucket: expected {NoSuchBucket} HTTP 404, got {(code, msg, status)}");
    }

    private static async Task<List<string>> Keys(IAmazonS3 s3, string bucket) =>
        [.. ((await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket })).S3Objects ?? []).Select(o => o.Key)];

    /// <summary>Call fn(); return the ClientError response it raises. Raises on an unexpected success.</summary>
    private static async Task<AwsError> Err(Func<Task> fn) =>
        await FidelityContext.CaptureError(fn) ?? throw Fail("expected a ClientError, the call unexpectedly succeeded");

    /// <summary>(Error.Code, Error.Message, HTTP status) of a ClientError response.</summary>
    private static (string? Code, string? Message, int Status) CodeMsgStatus(AwsError err) => (err.Code, err.Message, (int)err.StatusCode);
}
