// Translated from behavioral/s3/check_s3x_s3express_session_and_listing_cross_bucket_token_denied.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3x-s3express-session-and-listing-cross-bucket-token-denied</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3xS3expressSessionAndListingCrossBucketTokenDenied
{
    /// <summary>
    /// A ReadWrite CreateSession token minted for directory bucket A is AccessDenied on bucket B for
    /// GetObject and PutObject, leaving B's object untouched, while B's own token succeeds: a session
    /// token is scoped to the bucket that minted it, even for the principal owning both.
    /// </summary>
    [Test]
    [DisplayName("s3.s3x-s3express-session-and-listing-cross-bucket-token-denied")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucketA = await S3Fidelity.DirectoryBucket(ctx, s3, "sim2real-s3x-xa-");
        var bucketB = await S3Fidelity.DirectoryBucket(ctx, s3, "sim2real-s3x-xb-");
        const string key = "x/object.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucketA, Key = key, ContentBody = "bucket-a-original" });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucketB, Key = key, ContentBody = "bucket-b-original" });
        var (tokA, statsA) = S3Fidelity.SessionClient(ctx, await S3Fidelity.CreateSession(s3, bucketA, "ReadWrite"));
        var (tokB, statsB) = S3Fidelity.SessionClient(ctx, await S3Fidelity.CreateSession(s3, bucketB, "ReadWrite"));

        // Positive control: token A is live and valid on the bucket it was minted for.
        var home = await S3Fidelity.GetBytes(tokA, bucketA, key);
        Ensure(home.SequenceEqual("bucket-a-original"u8.ToArray()), $"token A could not read its own bucket: {Py.Repr(home)}");

        // The one thing under test: token A against bucket B.
        var got = await S3Fidelity.ErrorWithStatus(() => tokA.GetObjectAsync(bucketB, key));
        Ensure(
            got == ("AccessDenied", 403, "Access Denied"),
            $"bucket-A token GetObject on bucket B returned {S3Fidelity.Repr(got)}, expected ('AccessDenied', 403, 'Access Denied')");
        got = await S3Fidelity.ErrorWithStatus(() => tokA.PutObjectAsync(new PutObjectRequest { BucketName = bucketB, Key = key, ContentBody = "overwritten-by-a" }));
        Ensure(
            got == ("AccessDenied", 403, "Access Denied"),
            $"bucket-A token PutObject on bucket B returned {S3Fidelity.Repr(got)}, expected ('AccessDenied', 403, 'Access Denied')");

        // Effect absent, read through B's own token -- which is also the positive control that B is
        // reachable with a correctly scoped token.
        var bBody = await S3Fidelity.GetBytes(tokB, bucketB, key);
        Ensure(bBody.SequenceEqual("bucket-b-original"u8.ToArray()), $"refused cross-bucket PutObject changed bucket B: {Py.Repr(bBody)}");
        await tokB.PutObjectAsync(new PutObjectRequest { BucketName = bucketB, Key = key, ContentBody = "overwritten-by-b" });
        var bAfter = await S3Fidelity.GetBytes(tokB, bucketB, key);
        Ensure(bAfter.SequenceEqual("overwritten-by-b"u8.ToArray()), $"bucket-B token PutObject did not take effect: {Py.Repr(bAfter)}");
        foreach (var (label, stats, n) in new[] { ("A", statsA, 3), ("B", statsB, 3) })
        {
            Ensure(!stats.Ops.Contains("CreateSession"), $"the bucket-{label} session-pinned client minted its own session (ops {Py.Repr(stats.Ops)})");
            Ensure(
                stats.Pinned == stats.Ops.Count && stats.Ops.Count == n,
                $"token {label}: expected {n} calls signed with the minted session, got pinned={stats.Pinned} ops={Py.Repr(stats.Ops)}");
        }
    }
}
