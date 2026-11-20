// Translated from behavioral/s3/check_delete_bucket_with_metadata_table_configuration_blocked.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.delete-bucket-with-metadata-table-configuration-blocked</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteBucketWithMetadataTableConfigurationBlocked
{
    /// <summary>S3's refusal of a bucket that does not exist, as real S3 returns it.</summary>
    private static readonly (string, string) NoSuchBucket = ("NoSuchBucket", "The specified bucket does not exist");

    /// <summary>
    /// DeleteBucket on an empty bucket with an ACTIVE metadata journal configuration succeeds, and
    /// GetBucketMetadataConfiguration and a configuration change on the deleted bucket then fail with
    /// NoSuchBucket, the same answer a never-created bucket gets.
    /// </summary>
    [Test]
    [DisplayName("s3.delete-bucket-with-metadata-table-configuration-blocked")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;

        // safety net only: the happy path below deletes the bucket itself once the
        // assertions it depends on are satisfied, so this is a no-op on that path and
        // only matters if an earlier assertion fails first.
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-meta-");
        ctx.OnCleanup(() => S3Fidelity.SafeDeleteMetadataConfiguration(s3, bucket));

        await S3Fidelity.CreateMetadataConfiguration(s3, bucket);

        var status = await S3Fidelity.PollUntil(
            () => S3Fidelity.JournalStatus(s3, bucket),
            s => s is "ACTIVE" or "FAILED",
            TimeSpan.FromSeconds(180),
            TimeSpan.FromSeconds(3),
            last => $"journal table configuration reaching a terminal state not met within 180.0s; last={Py.Repr(last)}");
        Ensure(status == "ACTIVE", $"expected journal table configuration ACTIVE, got {status}");

        // overly-permissive guard: while the configuration is active, an UNRELATED
        // nonexistent bucket must still be rejected as NoSuchBucket, not silently reused.
        var stray = FidelityContext.UniqueName("sim2real-meta-none-");
        var strayCode = await Error(() => s3.GetBucketMetadataConfigurationAsync(new GetBucketMetadataConfigurationRequest { BucketName = stray }));
        Ensure(strayCode == NoSuchBucket, $"expected NoSuchBucket for a nonexistent bucket, got {S3Fidelity.Repr(strayCode)}");

        // DeleteBucket succeeds even with the metadata configuration still active --
        // it is not blocked as a wrong-state precondition.
        await s3.DeleteBucketAsync(bucket);

        // effect: the bucket's identity (and therefore its metadata configuration) is
        // gone -- callers must see NoSuchBucket, not a residual metadata-configuration
        // response or a NoSuchConfiguration that would imply the bucket still exists.
        var afterCode = await S3Fidelity.PollUntil(
            () => Error(() => s3.GetBucketMetadataConfigurationAsync(new GetBucketMetadataConfigurationRequest { BucketName = bucket })),
            code => code != Succeeded,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(3),
            last => $"metadata configuration read failing after bucket deletion not met within 60.0s; last={S3Fidelity.Repr(last)}");
        Ensure(afterCode == NoSuchBucket, $"expected NoSuchBucket after bucket deletion, got {S3Fidelity.Repr(afterCode)}");

        // the dead identity is not just unreadable -- a mutation against it fails too.
        var mutationCode = await Error(() => S3Fidelity.DeleteMetadataConfiguration(s3, bucket));
        Ensure(
            mutationCode == NoSuchBucket,
            $"expected NoSuchBucket deleting the metadata configuration of a deleted bucket, got {S3Fidelity.Repr(mutationCode)}");
    }

    /// <summary>What Error returns when its call succeeds; no (code, message) pair can equal it.</summary>
    private static readonly (string?, string?) Succeeded = ("<the call succeeded>", null);

    /// <summary>
    /// The ClientError (Code, Message) of the call, or Succeeded when it succeeds. Succeeded equals no
    /// expected error, so an unexpected success fails every assert that names one.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> call) =>
        await FidelityContext.CaptureError(call) is { } error ? (error.Code, error.Message) : Succeeded;
}
