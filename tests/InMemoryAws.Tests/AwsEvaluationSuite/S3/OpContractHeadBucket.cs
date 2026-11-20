// Translated from behavioral/s3/check_op_contract_head_bucket.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-headBucket</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractHeadBucket
{
    private const string BucketPrefix = "sim2real-behav-";

    /// <summary>HeadBucket succeeds (200) for an existing bucket; returns a 404 for a nonexistent one.</summary>
    [Test]
    [DisplayName("s3.op-contract-headBucket")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var bucket = await S3Fidelity.CreateBucket(ctx);
        var s3 = ctx.S3;

        var resp = await s3.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket });
        Ensure(resp.HttpStatusCode == HttpStatusCode.OK, $"expected 200, got {(int)resp.HttpStatusCode}");
        // HeadBucket surfaces the resolved region for the bucket; must match where it was created
        Ensure(resp.BucketRegion == ctx.Region, $"BucketRegion {Py.Repr(resp.BucketRegion)} != harness region {Py.Repr(ctx.Region)}");

        // distinct error boundary: nonexistent bucket -> 404
        var missing = FidelityContext.UniqueName(BucketPrefix + "missing-");
        var e = await FidelityContext.CaptureError(() => s3.HeadBucketAsync(new HeadBucketRequest { BucketName = missing }))
            ?? throw Fail("expected head_bucket on missing bucket to raise");
        var status = (int)e.StatusCode;
        Ensure(status == 404, $"expected HTTP 404, got {status}");
        // botocore reports a bodiless HEAD error's code as its status, "404"
        var (code, _) = S3Fidelity.Botocore(e);
        Ensure(code == "404", $"expected error code '404', got {code}");
    }
}
