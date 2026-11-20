// Translated from behavioral/s3/check_conc_s3_conditional_writes_lock_stale_holder_release_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.conc-s3-conditional-writes-lock-stale-holder-release-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcS3ConditionalWritesLockStaleHolderReleaseRejected
{
    private static readonly (string, string) PreconditionFailed = ("PreconditionFailed", "At least one of the pre-conditions you specified did not hold");
    private static readonly (string, string) NoSuchKey = ("NoSuchKey", "The specified key does not exist.");

    /// <summary>
    /// S3 as a lock (If-None-Match:* acquire, If-Match renew/release): once A's lock passes to B,
    /// A's stale ETag is refused with 412 for both release and renew, so B keeps the lock.
    /// Controls: A's own release and B's renew and release with B's current ETag succeed.
    /// </summary>
    [Test]
    [DisplayName("s3.conc-s3-conditional-writes-lock-stale-holder-release-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateCondwBucket(ctx);
        var lockKey = FidelityContext.UniqueName("locks/job-");
        var tokenA = Encoding.UTF8.GetBytes(FidelityContext.UniqueName("holder-a-"));
        var tokenB = Encoding.UTF8.GetBytes(FidelityContext.UniqueName("holder-b-"));

        Task<PutObjectResponse> Put(byte[] body, string? ifMatch = null, string? ifNoneMatch = null) =>
            s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = lockKey, InputStream = new MemoryStream(body), IfMatch = ifMatch, IfNoneMatch = ifNoneMatch });
        Task<DeleteObjectResponse> Delete(string ifMatch) =>
            s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = lockKey, IfMatch = ifMatch });

        // A acquires.
        var etagA = (await Put(tokenA, ifNoneMatch: "*")).ETag;

        // B contends and is refused; A still holds.
        var contend = await S3Fidelity.ExpectError(() => Put(tokenB, ifNoneMatch: "*"), "B's acquire of a held lock SUCCEEDED");
        Ensure(Refusal(contend) == PreconditionFailed, $"B's acquire of a held lock returned {Refusal(contend)}; want {PreconditionFailed}");
        Ensure(await IsHolder(s3, bucket, lockKey, tokenA, etagA), "B's refused acquire changed the lock");

        // POSITIVE CONTROL: A releases its own lock with its own ETag.
        var released = await Delete(etagA);
        Ensure(released.HttpStatusCode == HttpStatusCode.NoContent, $"A's release returned {(int)released.HttpStatusCode}; want 204");
        var free = await S3Fidelity.ExpectError(() => s3.GetObjectAsync(bucket, lockKey), "after A's release the lock is still readable");
        Ensure(Refusal(free) == NoSuchKey, $"after A's release the lock read gave {Refusal(free)}; want {NoSuchKey}");

        // B acquires the now-free lock.
        var etagB = (await Put(tokenB, ifNoneMatch: "*")).ETag;
        Ensure(etagB != etagA, $"B's lock has A's ETag {Py.Repr(etagA)}; tokens must differ");

        // --- NEGATIVE: A's stale release is refused and B keeps the lock.
        var staleRelease = await S3Fidelity.ExpectError(() => Delete(etagA), "A's stale release SUCCEEDED and deleted B's lock: two holders can now run concurrently");
        Ensure(Refusal(staleRelease) == PreconditionFailed, $"A's stale release returned {Refusal(staleRelease)}; want {PreconditionFailed}");
        Ensure(staleRelease.StatusCode == HttpStatusCode.PreconditionFailed, $"stale release HTTP status {(int)staleRelease.StatusCode}");
        Ensure(await IsHolder(s3, bucket, lockKey, tokenB, etagB), "A's refused release changed B's lock");

        // --- NEGATIVE: A's stale renew is refused and B keeps the lock.
        var staleRenew = await S3Fidelity.ExpectError(() => Put([.. tokenA, .. "-renewed"u8], ifMatch: etagA), "A's stale renew SUCCEEDED and overwrote B's lock");
        Ensure(Refusal(staleRenew) == PreconditionFailed, $"A's stale renew returned {Refusal(staleRenew)}; want {PreconditionFailed}");
        Ensure(await IsHolder(s3, bucket, lockKey, tokenB, etagB), "A's refused renew changed B's lock");

        // POSITIVE CONTROL: B renews with its own ETag, then releases with the renewed ETag.
        byte[] renewed = [.. tokenB, .. "-renewed"u8];
        var etagB2 = (await Put(renewed, ifMatch: etagB)).ETag;
        Ensure(await IsHolder(s3, bucket, lockKey, renewed, etagB2), "B's renew did not land");
        var oldB = await S3Fidelity.ExpectError(() => Delete(etagB), "release with B's pre-renew ETag SUCCEEDED");
        Ensure(Refusal(oldB) == PreconditionFailed, $"release with B's pre-renew ETag returned {Refusal(oldB)}; want {PreconditionFailed}");
        await Delete(etagB2);
        var end = await S3Fidelity.ExpectError(() => s3.GetObjectAsync(bucket, lockKey), "after B's release the lock is still readable");
        Ensure(Refusal(end) == NoSuchKey, $"after B's release the lock read gave {Refusal(end)}; want {NoSuchKey}");
    }

    /// <summary><c>_refusal(exc)</c>: <c>(code, message)</c>.</summary>
    private static (string?, string?) Refusal(AwsError error) => (error.Code, error.Message);

    /// <summary><c>_holder(s3, bucket, key) == (token, etag)</c>.</summary>
    private static async Task<bool> IsHolder(IAmazonS3 s3, string bucket, string key, byte[] token, string etag)
    {
        using var got = await s3.GetObjectAsync(bucket, key);
        return (await S3Fidelity.ReadBody(got)).AsSpan().SequenceEqual(token) && got.ETag == etag;
    }
}
