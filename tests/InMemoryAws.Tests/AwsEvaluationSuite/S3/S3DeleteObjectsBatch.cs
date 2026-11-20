// Translated from behavioral/s3/check_s3_delete_objects_batch.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-delete-objects-batch</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3DeleteObjectsBatch
{
    private const string ErrNoSuchKey = "NoSuchKey";

    /// <summary>DeleteObjects (batch) removes all listed keys; response lists them; GETs then 404.</summary>
    [Test]
    [DisplayName("s3.s3-delete-objects-batch")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var keys = Enumerable.Range(0, 5).Select(i => $"batch/obj-{i}.txt").ToList();
        foreach (var k in keys)
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = k, ContentBody = $"content-{k}" });
        }

        var resp = await s3.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = bucket, Objects = [.. keys.Select(k => new KeyVersion { Key = k })], Quiet = false });
        var deleted = (resp.DeletedObjects ?? []).Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        Ensure(deleted.SetEquals(keys), $"expected all keys in Deleted, got {Py.Repr(deleted.ToList())}");
        Ensure((resp.DeleteErrors ?? []).Count == 0, $"expected no per-key errors, got {Py.Repr((resp.DeleteErrors ?? []).Select(e => e.Code).ToList())}");

        foreach (var k in keys)
        {
            var err = await FidelityContext.CaptureError(() => s3.GetObjectAsync(bucket, k)) ?? throw Fail($"GetObject of deleted {k} succeeded; expected a refusal");
            Ensure(err.Code == ErrNoSuchKey, $"expected {ErrNoSuchKey} for deleted {k}, got {err}");
            Ensure(err.Message == "The specified key does not exist.", err.Message ?? "None");
        }
    }
}
