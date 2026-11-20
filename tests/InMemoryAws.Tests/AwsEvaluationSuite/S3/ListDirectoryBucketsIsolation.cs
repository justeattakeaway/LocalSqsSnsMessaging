// Translated from behavioral/s3/check_list_directory_buckets_isolation.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.list-directory-buckets-isolation</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListDirectoryBucketsIsolation
{
    /// <summary>ListDirectoryBuckets returns only directory (Express) buckets, excluding general-purpose buckets.</summary>
    [Test]
    [DisplayName("s3.list-directory-buckets-isolation")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var gpBucket = await S3Fidelity.CreateBucket(ctx, "sim2real-gp-");
        var (dirBucket, _) = await S3Fidelity.CreateDirectoryBucket(ctx, s3, FidelityContext.UniqueName("sim2real-dir-"), S3Fidelity.ExpressZoneIds[ctx.Region]);

        var gpListed = ((await s3.ListBucketsAsync(new ListBucketsRequest())).Buckets ?? []).Select(b => b.BucketName).ToHashSet();
        Ensure(gpListed.Contains(gpBucket), $"expected {gpBucket} in ListBuckets output");
        Ensure(!gpListed.Contains(dirBucket), $"directory bucket {dirBucket} leaked into ListBuckets");

        var dirListed = ((await s3.ListDirectoryBucketsAsync(new ListDirectoryBucketsRequest())).Buckets ?? []).Select(b => b.BucketName).ToHashSet();
        Ensure(dirListed.Contains(dirBucket), $"expected {dirBucket} in ListDirectoryBuckets output");
        Ensure(!dirListed.Contains(gpBucket), $"general-purpose bucket {gpBucket} leaked into ListDirectoryBuckets");
    }
}
