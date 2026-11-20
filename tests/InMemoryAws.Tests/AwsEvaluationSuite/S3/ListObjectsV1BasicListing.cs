// Translated from behavioral/s3/check_list_objects_v1_basic_listing.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.list-objects-v1-basic-listing</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListObjectsV1BasicListing
{
    /// <summary>Legacy ListObjects enumerates keys in a real bucket, NoSuchBucket for a missing one.</summary>
    [Test]
    [DisplayName("s3.list-objects-v1-basic-listing")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-s3-behav-");
        string[] keys = ["alpha.txt", "beta.txt", "gamma/nested.txt"];
        foreach (var k in keys)
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = k, ContentBody = "payload" });
        }

        var listed = await s3.ListObjectsAsync(new ListObjectsRequest { BucketName = bucket });
        var names = (listed.S3Objects ?? []).Select(o => o.Key).Order(StringComparer.Ordinal).ToList();
        var sorted = keys.Order(StringComparer.Ordinal).ToList();
        Ensure(names.SequenceEqual(sorted), $"expected {Py.Repr(sorted)}, got {Py.Repr(names)}");
        Ensure(listed.Name == bucket, $"expected Name={Py.Repr(bucket)}, got {Py.Repr(listed.Name)}");
        Ensure(listed.IsTruncated == false, $"expected IsTruncated=False, got {Py.Repr(listed.IsTruncated)}");
        Ensure(listed.MaxKeys == 1000, $"expected the documented default MaxKeys=1000, got {Py.Repr(listed.MaxKeys)}");

        // Positive control established above; now break the one precondition: bucket existence.
        var missingBucket = FidelityContext.UniqueName("sim2real-s3-missing-").ToLowerInvariant();
        var err = await FidelityContext.CaptureError(() => s3.ListObjectsAsync(new ListObjectsRequest { BucketName = missingBucket }))
            ?? throw Fail("expected a ClientError, but the call SUCCEEDED");
        Ensure(err.Code == "NoSuchBucket", $"expected NoSuchBucket, got {err}");
        Ensure(err.Message == "The specified bucket does not exist", $"{err}");

        // The forbidden call did not create a bucket as a side effect.
        var existing = ((await s3.ListBucketsAsync(new ListBucketsRequest())).Buckets ?? []).Select(b => b.BucketName).ToList();
        Ensure(!existing.Contains(missingBucket), $"{missingBucket} unexpectedly exists after a failed ListObjects call");

        // Restore: the missing precondition is bucket existence, and unlike a malformed input it can
        // be fixed within the check — creating the bucket must make the identical call succeed.
        var create = new PutBucketRequest { BucketName = missingBucket, UseClientRegion = false };
        if (ctx.Region != "us-east-1")
        {
            create.PutBucketConfiguration = new PutBucketConfiguration { LocationConstraint = new BucketLocationConstraint(ctx.Region) };
        }

        await s3.PutBucketAsync(create);
        ctx.OnCleanup(() => S3Fidelity.EmptyAndDelete(s3, missingBucket));
        var restored = await s3.ListObjectsAsync(new ListObjectsRequest { BucketName = missingBucket });
        Ensure(
            restored.S3Objects is null or { Count: 0 },
            $"expected an empty listing for the newly-created {missingBucket}, got {Py.Repr(restored.S3Objects?.Select(o => o.Key).ToList())}");
    }
}
