// Translated from behavioral/s3/check_if_match_success.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.if-match-success</summary>
[Category("AwsEvaluationSuite")]
public sealed class IfMatchSuccess
{
    /// <summary>GetObject with If-Match matching the ETag succeeds.</summary>
    [Test]
    [DisplayName("s3.if-match-success")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var body = Encoding.UTF8.GetBytes("conditional-match-test");
        var putResp = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "cond.txt", InputStream = new MemoryStream(body) });
        var etag = putResp.ETag;

        // Should succeed
        using var resp = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "cond.txt", EtagToMatch = etag });
        var got = await S3Fidelity.ReadBody(resp);
        Ensure(got.AsSpan().SequenceEqual(body), $"expected {Py.Repr(body)} for a matching If-Match, got {Py.Repr(got)}");
    }
}
