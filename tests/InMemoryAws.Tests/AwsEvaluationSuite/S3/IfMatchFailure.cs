// Translated from behavioral/s3/check_if_match_failure.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.if-match-failure</summary>
[Category("AwsEvaluationSuite")]
public sealed class IfMatchFailure
{
    private const string ErrPreconditionFailed = "PreconditionFailed";

    /// <summary>
    /// GetObject with If-Match NOT matching returns PreconditionFailed (412); the identical
    /// call with the object's actual current ETag succeeds, proving the rejection tracks the
    /// mismatch and is not a blanket refusal of every conditional GetObject.
    /// </summary>
    [Test]
    [DisplayName("s3.if-match-failure")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var body = Encoding.UTF8.GetBytes("original");
        var putResp = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "cond.txt", InputStream = new MemoryStream(body) });
        var currentEtag = putResp.ETag;

        // Positive control: the correct current ETag must be accepted.
        using (var control = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "cond.txt", EtagToMatch = currentEtag }))
        {
            Ensure((await S3Fidelity.ReadBody(control)).AsSpan().SequenceEqual(body), "expected the matching If-Match GetObject to succeed");
        }

        // Break exactly one thing: a mismatched ETag must be rejected.
        var err = await FidelityContext.CaptureError(async () =>
        {
            using var _ = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "cond.txt", EtagToMatch = "\"bogus-etag\"" });
        }) ?? throw Fail("expected a ClientError, but the call succeeded");
        Ensure(err.Code == ErrPreconditionFailed, $"expected {ErrPreconditionFailed}, got {err}");
        Ensure(err.Message == "At least one of the pre-conditions you specified did not hold", err.ToString());
    }
}
