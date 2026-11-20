// Translated from behavioral/s3/check_list_directory_buckets_rejects_regular_bucket_ops.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.list-directory-buckets-rejects-regular-bucket-ops</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListDirectoryBucketsRejectsRegularBucketOps
{
    /// <summary>
    /// A general-purpose bucket appears in ListBuckets but never in ListDirectoryBuckets, while a real S3
    /// Express directory bucket does appear there.
    /// </summary>
    [Test]
    [DisplayName("s3.list-directory-buckets-rejects-regular-bucket-ops")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");
        var (dirBucket, _) = await S3Fidelity.CreateDirectoryBucket(ctx, s3, FidelityContext.UniqueName("sim2real-dir-"), S3Fidelity.ExpressZoneIds[ctx.Region]);

        var allBuckets = ((await s3.ListBucketsAsync(new ListBucketsRequest())).Buckets ?? []).Select(b => b.BucketName).ToHashSet();
        Ensure(allBuckets.Contains(bucket), $"sanity check failed: {bucket} missing from ListBuckets");

        var directoryBucketNames = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        for (var i = 0; i < 50; i++)
        {
            // bounded pagination guard
            var resp = await s3.ListDirectoryBucketsAsync(new ListDirectoryBucketsRequest { MaxDirectoryBuckets = 1000, ContinuationToken = token });
            directoryBucketNames.UnionWith((resp.Buckets ?? []).Select(b => b.BucketName));
            token = resp.ContinuationToken;
            if (string.IsNullOrEmpty(token))
            {
                break;
            }
        }

        // Positive control: the listing is populated, not trivially empty.
        Ensure(
            directoryBucketNames.Contains(dirBucket),
            $"directory bucket {dirBucket} missing from ListDirectoryBuckets result " +
            $"{Py.Repr(directoryBucketNames.Order(StringComparer.Ordinal).ToList())} (cannot distinguish correct scoping from an " +
            "unimplemented, always-empty ListDirectoryBuckets)");
        Ensure(
            !directoryBucketNames.Contains(bucket),
            $"general-purpose bucket {bucket} incorrectly appeared in ListDirectoryBuckets " +
            "result (directory-bucket-only namespace leaked general-purpose bucket)");

        // A general-purpose bucket also never carries the directory-bucket naming suffix.
        Ensure(!bucket.EndsWith("--x-s3", StringComparison.Ordinal), $"unexpected directory-bucket-style name: {bucket}");
    }
}
