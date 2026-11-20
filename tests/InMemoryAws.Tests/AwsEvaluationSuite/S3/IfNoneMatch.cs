// Translated from behavioral/s3/check_if_none_match.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.if-none-match</summary>
[Category("AwsEvaluationSuite")]
public sealed class IfNoneMatch
{
    /// <summary>
    /// GetObject with If-None-Match matching the ETag returns 304 Not Modified; a
    /// non-matching value still serves the object, and an unconditional read afterward
    /// is unaffected.
    /// </summary>
    [Test]
    [DisplayName("s3.if-none-match")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var body = Encoding.UTF8.GetBytes("none-match-test");
        var putResp = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "nm.txt", InputStream = new MemoryStream(body) });
        var etag = putResp.ETag;

        using (var control = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "nm.txt", EtagToNotMatch = "\"not-the-etag\"" }))
        {
            Ensure((await S3Fidelity.ReadBody(control)).AsSpan().SequenceEqual(body), "positive control: mismatching If-None-Match must still serve the object");
        }

        var exc = await FidelityContext.CaptureError(async () =>
        {
            using var _ = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "nm.txt", EtagToNotMatch = etag });
        }) ?? throw Fail("expected 304 but request succeeded");
        Ensure(exc.StatusCode == HttpStatusCode.NotModified, $"expected 304, got status {(int)exc.StatusCode}");

        Ensure((await S3Fidelity.GetBytes(s3, bucket, "nm.txt")).AsSpan().SequenceEqual(body), "unconditional GetObject must still succeed after the 304");
    }
}
