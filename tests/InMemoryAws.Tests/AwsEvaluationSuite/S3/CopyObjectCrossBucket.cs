// Translated from behavioral/s3/check_copy_object_cross_bucket.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.copy-object-cross-bucket</summary>
[Category("AwsEvaluationSuite")]
public sealed class CopyObjectCrossBucket
{
    /// <summary>CopyObject across buckets works, and leaves the source untouched.</summary>
    [Test]
    [DisplayName("s3.copy-object-cross-bucket")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var srcBucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var dstBucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var body = Encoding.ASCII.GetBytes("cross-bucket-copy");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = srcBucket, Key = "origin.bin", InputStream = new MemoryStream(body) });
        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            DestinationBucket = dstBucket,
            DestinationKey = "copied.bin",
            SourceBucket = srcBucket,
            SourceKey = "origin.bin",
        });
        var got = await S3Fidelity.GetBytes(s3, dstBucket, "copied.bin");
        Ensure(got.AsSpan().SequenceEqual(body), $"cross-bucket copy did not preserve bytes: expected {Py.Repr(body)}, got {Py.Repr(got)}");

        var srcStill = await S3Fidelity.GetBytes(s3, srcBucket, "origin.bin");
        Ensure(
            srcStill.AsSpan().SequenceEqual(body),
            $"CopyObject must not remove or alter the source object: expected {Py.Repr(body)}, got {Py.Repr(srcStill)}");
    }
}
