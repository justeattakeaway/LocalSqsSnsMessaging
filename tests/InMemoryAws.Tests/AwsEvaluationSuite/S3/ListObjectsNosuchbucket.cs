// Translated from behavioral/s3/check_list_objects_nosuchbucket.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.list-objects-nosuchbucket</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListObjectsNosuchbucket
{
    /// <summary>ListObjects against a bucket that does not exist returns NoSuchBucket rather than an empty listing.</summary>
    [Test]
    [DisplayName("s3.list-objects-nosuchbucket")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;

        // Positive control: ListObjects succeeds against a bucket that does exist, so the
        // negative below cannot pass merely because ListObjects is broken outright.
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-lsctrl-");
        var resp = await s3.ListObjectsAsync(new ListObjectsRequest { BucketName = bucket });
        Ensure(resp.S3Objects is null or { Count: 0 }, $"expected empty listing, got {Py.Repr(resp.S3Objects?.Select(o => o.Key).ToList())}");

        // Break the one precondition: bucket existence.
        var missingBucket = FidelityContext.UniqueName("sim2real-nobucket-");
        var err = await FidelityContext.CaptureError(() => s3.ListObjectsAsync(new ListObjectsRequest { BucketName = missingBucket }))
            ?? throw Fail("expected a ClientError, but the call succeeded");
        Ensure(err.Code == "NoSuchBucket", $"expected NoSuchBucket, got {err}");
        Ensure(err.Message == "The specified bucket does not exist", $"{err}");

        // Independent read: the failed call did not create the bucket as a side effect.
        var existing = ((await s3.ListBucketsAsync(new ListBucketsRequest())).Buckets ?? []).Select(b => b.BucketName).ToList();
        Ensure(!existing.Contains(missingBucket), $"{missingBucket} unexpectedly exists after a failed ListObjects call");
    }
}
