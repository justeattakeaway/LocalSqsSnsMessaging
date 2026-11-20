// Translated from behavioral/s3/check_directory_buckets_listing.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.directory-buckets-listing</summary>
[Category("AwsEvaluationSuite")]
public sealed class DirectoryBucketsListing
{
    /// <summary>ListDirectoryBuckets returns only S3 Express directory buckets, disjoint from ListBuckets.</summary>
    [Test]
    [DisplayName("s3.directory-buckets-listing")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var (dirbucket, _) = await S3Fidelity.CreateDirectoryBucket(
            ctx, s3, FidelityContext.UniqueName("sim2real-dirbkt-"), S3Fidelity.ExpressZoneIds[ctx.Region]);
        var regular = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        var dirListed = await s3.ListDirectoryBucketsAsync(new ListDirectoryBucketsRequest());
        var dirNames = (dirListed.Buckets ?? []).Select(b => b.BucketName).ToList();
        Ensure(dirNames.Contains(dirbucket), $"expected directory bucket {dirbucket} in {Py.Repr(dirNames)}");
        Ensure(!dirNames.Contains(regular), $"regular bucket {regular} unexpectedly in ListDirectoryBuckets: {Py.Repr(dirNames)}");

        var regListed = await s3.ListBucketsAsync(new ListBucketsRequest());
        var regNames = (regListed.Buckets ?? []).Select(b => b.BucketName).ToList();
        Ensure(regNames.Contains(regular), $"expected regular bucket {regular} in {Py.Repr(regNames)}");
        Ensure(!regNames.Contains(dirbucket), $"directory bucket {dirbucket} unexpectedly in ListBuckets: {Py.Repr(regNames)}");
    }
}
