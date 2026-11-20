// Translated from behavioral/s3/check_s3x_s3express_session_and_listing_readonly_session_put_denied.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3x-s3express-session-and-listing-readonly-session-put-denied</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3xS3expressSessionAndListingReadonlySessionPutDenied
{
    /// <summary>
    /// A SessionMode=ReadOnly CreateSession token is refused on PutObject with AccessDenied and can still
    /// read, while a ReadWrite token succeeds on the byte-identical PutObject to the same bucket.
    /// </summary>
    [Test]
    [DisplayName("s3.s3x-s3express-session-and-listing-readonly-session-put-denied")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.DirectoryBucket(ctx, s3, "sim2real-s3x-ro-");
        const string key = "ro/target.txt";
        var original = "written-by-readwrite-v1"u8.ToArray();
        var replacement = "attempted-by-readonly-v2"u8.ToArray();
        var (rw, rwStats) = S3Fidelity.SessionClient(ctx, await S3Fidelity.CreateSession(s3, bucket, "ReadWrite"));
        var (ro, roStats) = S3Fidelity.SessionClient(ctx, await S3Fidelity.CreateSession(s3, bucket, "ReadOnly"));
        await rw.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(original) });

        Task<PutObjectResponse> IdenticalPut(IAmazonS3 client) =>
            client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(replacement) });

        // The one thing under test: the ReadOnly token on PutObject.
        var refusal = await S3Fidelity.ErrorWithStatus(() => IdenticalPut(ro));
        Ensure(
            refusal == ("AccessDenied", 403, "Access Denied"),
            $"ReadOnly session PutObject returned {S3Fidelity.Repr(refusal)}, expected ('AccessDenied', 403, 'Access Denied')");

        // Effect absent: the object still holds the ReadWrite write, read through the ReadOnly token
        // itself -- which also proves that token is live and scoped to this bucket, so the refusal
        // above is about the MODE, not a dead or foreign token.
        var still = await S3Fidelity.GetBytes(ro, bucket, key);
        Ensure(still.SequenceEqual(original), $"refused PutObject changed the object: now {Py.Repr(still)}, expected {Py.Repr(original)}");

        // Positive control: the byte-identical call through the ReadWrite token succeeds.
        await IdenticalPut(rw);
        var after = await S3Fidelity.GetBytes(rw, bucket, key);
        Ensure(after.SequenceEqual(replacement), $"ReadWrite PutObject did not take effect: read {Py.Repr(after)}");
        foreach (var (label, stats, n) in new[] { ("ReadWrite", rwStats, 3), ("ReadOnly", roStats, 2) })
        {
            Ensure(!stats.Ops.Contains("CreateSession"), $"the {label} session-pinned client minted its own session (ops {Py.Repr(stats.Ops)})");
            Ensure(
                stats.Pinned == stats.Ops.Count && stats.Ops.Count == n,
                $"{label}: expected {n} calls signed with the minted session, got pinned={stats.Pinned} ops={Py.Repr(stats.Ops)}");
        }
    }
}
