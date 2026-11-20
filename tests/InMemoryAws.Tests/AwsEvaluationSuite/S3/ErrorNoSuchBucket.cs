// Translated from behavioral/s3/check_error_no_such_bucket.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.error-no-such-bucket</summary>
[Category("AwsEvaluationSuite")]
public sealed class ErrorNoSuchBucket
{
    private const string ErrNoSuchBucket = "NoSuchBucket";

    /// <summary>
    /// GetObject on an existing bucket and key returns its bytes, and on a bucket that never existed
    /// fails with NoSuchBucket and its exact message.
    /// </summary>
    [Test]
    [DisplayName("s3.error-no-such-bucket")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;

        var realBucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = realBucket, Key = "probe", ContentBody = "ok" });

        var control = await S3Fidelity.GetBytes(s3, realBucket, "probe");
        Ensure(control.AsSpan().SequenceEqual("ok"u8), "positive control: GetObject on a real bucket/key must succeed");

        var ghost = $"sim2real-behav-ghost-{Guid.NewGuid():N}"[..29];
        var err = await FidelityContext.CaptureError(() => s3.GetObjectAsync(ghost, "any"))
            ?? throw Fail($"GetObject on never-created bucket {Py.Repr(ghost)} succeeded; expected a refusal");
        Ensure(err.Code == ErrNoSuchBucket, $"expected {ErrNoSuchBucket}, got {err}");
        Ensure(err.Message == "The specified bucket does not exist", err.Message ?? "None");
    }
}
