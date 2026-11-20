// Translated from behavioral/s3/check_concurrent_versioned_puts_no_lost_writes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.concurrent-versioned-puts-no-lost-writes</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentVersionedPutsNoLostWrites
{
    /// <summary>
    /// N concurrent PutObject calls on a versioned bucket for the same key each create their own
    /// version; none are lost.
    /// </summary>
    [Test]
    [DisplayName("s3.concurrent-versioned-puts-no-lost-writes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = name,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });
        const string key = "race-versions.txt";
        const int n = 8;

        Task<PutObjectResponse> Attempt(int i) => s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = name,
            Key = key,
            InputStream = new MemoryStream(Encoding.UTF8.GetBytes($"payload-{i}")),
        });

        var results = await S3CoreFidelity.RunConcurrent(ctx, [.. Enumerable.Range(0, n).Select(i => (Func<Task<PutObjectResponse>>)(() => Attempt(i)))]);
        var nOk = results.Count(r => r is not Exception);
        Ensure(
            nOk == n,
            $"expected all {n} concurrent writes to succeed, got {nOk}; errors={Py.Repr(S3CoreFidelity.ConcurrentErrorCodes(results))}");
        var versionIds = results.OfType<PutObjectResponse>().Select(r => r.VersionId).ToHashSet(StringComparer.Ordinal);
        Ensure(versionIds.Count == n, $"expected {n} distinct version ids, got {versionIds.Count}: lost-write under contention");
        var versions = (await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = name, Prefix = key })).Versions ?? [];
        var listedIds = versions.Select(v => v.VersionId).ToHashSet(StringComparer.Ordinal);
        Ensure(listedIds.SetEquals(versionIds), "ListObjectVersions must account for every version created under contention");
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i] is not PutObjectResponse r)
            {
                continue;
            }

            using var get = await s3.GetObjectAsync(new GetObjectRequest { BucketName = name, Key = key, VersionId = r.VersionId });
            var body = await S3Fidelity.ReadBody(get);
            var expected = Encoding.UTF8.GetBytes($"payload-{i}");
            Ensure(
                body.AsSpan().SequenceEqual(expected),
                $"version {r.VersionId} (write #{i}) holds {Py.Repr(body)}, expected {Py.Repr(expected)}: "
                + "content swapped between versions under contention");
        }
    }
}
