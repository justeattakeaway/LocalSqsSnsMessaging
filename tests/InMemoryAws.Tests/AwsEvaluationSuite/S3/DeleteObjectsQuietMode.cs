// Translated from behavioral/s3/check_delete_objects_quiet_mode.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.delete-objects-quiet-mode</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteObjectsQuietMode
{
    /// <summary>DeleteObjects with Quiet=True omits the per-key Deleted entries while still removing every key.</summary>
    [Test]
    [DisplayName("s3.delete-objects-quiet-mode")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        string[] keys = ["q/1.txt", "q/2.txt", "q/3.txt"];
        foreach (var k in keys)
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = k, ContentBody = "x" });
        }

        var before = ((await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket })).S3Objects ?? []).Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        Ensure(before.SetEquals(keys), $"expected all keys present before delete, got {Py.Repr(before.ToList())}");

        var resp = await s3.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = bucket, Objects = [.. keys.Select(k => new KeyVersion { Key = k })], Quiet = true });

        Ensure((resp.DeletedObjects ?? []).Count == 0, $"Quiet=True must suppress Deleted entries, got {Py.Repr((resp.DeletedObjects ?? []).Select(d => d.Key).ToList())}");
        Ensure((resp.DeleteErrors ?? []).Count == 0, $"unexpected errors: {Py.Repr((resp.DeleteErrors ?? []).Select(e => e.Code).ToList())}");
        var remaining = (await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket })).S3Objects ?? [];
        Ensure(remaining.Count == 0, $"all keys should be gone, still present: {Py.Repr(remaining.Select(o => o.Key).ToList())}");
    }
}
