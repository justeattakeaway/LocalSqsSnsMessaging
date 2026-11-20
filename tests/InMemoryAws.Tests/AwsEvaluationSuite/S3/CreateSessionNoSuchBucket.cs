// Translated from behavioral/s3/check_create_session_no_such_bucket.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.create-session-no-such-bucket</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateSessionNoSuchBucket
{
    /// <summary>
    /// CreateSession on a nonexistent directory bucket raises NoSuchBucket with AWS's message, while it
    /// succeeds on a real one; ListDirectoryBuckets confirms the missing name is absent.
    /// </summary>
    [Test]
    [DisplayName("s3.create-session-no-such-bucket")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var baseName = FidelityContext.UniqueName("sim2real-behav-sess-").ToLowerInvariant().TrimEnd('-');
        var (bucket, az) = await S3Fidelity.CreateDirectoryBucket(ctx, s3, baseName, S3Fidelity.ExpressZoneIds[ctx.Region]);

        // Positive control: CreateSession succeeds on a real directory bucket.
        var control = await S3Fidelity.CreateSession(s3, bucket);
        Ensure(!string.IsNullOrEmpty(control.Credentials.AccessKeyId), "positive control: expected session credentials");

        // The AZ segment must belong to the region under test, otherwise S3 Express endpoint
        // resolution cannot build a resolvable hostname and the request never reaches the service.
        var missing = $"{baseName}-missing--{az}--x-s3";
        var listedBefore = await DirectoryBucketNames(s3);
        Ensure(!listedBefore.Contains(missing), $"bucket {missing} unexpectedly already present in {Py.Repr(listedBefore)}");
        var err = await S3Fidelity.ErrorPair(() => S3Fidelity.CreateSession(s3, missing), "expected a ClientError, but the call succeeded");
        Ensure(
            err == ("NoSuchBucket", "The specified bucket does not exist."),
            $"expected NoSuchBucket with AWS's exact message, got {S3Fidelity.Repr(err)}");

        // Effect absent: an independent read confirms the bucket does not exist.
        var listedAfter = await DirectoryBucketNames(s3);
        Ensure(!listedAfter.Contains(missing), $"bucket {missing} unexpectedly present in {Py.Repr(listedAfter)}");
    }

    private static async Task<List<string>> DirectoryBucketNames(IAmazonS3 s3) =>
        [.. ((await s3.ListDirectoryBucketsAsync(new ListDirectoryBucketsRequest())).Buckets ?? []).Select(b => b.BucketName)];
}
