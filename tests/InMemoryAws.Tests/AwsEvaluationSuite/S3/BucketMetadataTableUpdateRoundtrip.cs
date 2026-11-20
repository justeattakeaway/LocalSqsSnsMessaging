// Translated from behavioral/s3/check_bucket_metadata_table_update_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-metadata-table-update-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketMetadataTableUpdateRoundtrip
{
    /// <summary>
    /// Metadata table sub-configurations (annotation/inventory/journal) can be updated in place after
    /// creation.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-metadata-table-update-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await S3Fidelity.CreateMetadataConfiguration(s3, bucket, inventory: "DISABLED");

        Task<MetadataConfigurationResult> Get() => S3Fidelity.MetadataResult(s3, bucket);

        await S3Fidelity.PollMetadata(
            Get,
            r => r.JournalTableConfigurationResult?.TableStatus == "ACTIVE",
            timeoutS: 120,
            intervalS: 5,
            description: "journal table to become ACTIVE before updating sub-configs");

        // Journal: flip on record expiration in place.
        await S3Fidelity.UpdateJournal(s3, bucket, "ENABLED", 7);
        var updatedJournal = (await Get()).JournalTableConfigurationResult.RecordExpiration;
        Ensure(updatedJournal.Expiration?.Value == "ENABLED", S3Fidelity.Repr(updatedJournal));
        Ensure(updatedJournal.Days == 7, S3Fidelity.Repr(updatedJournal));

        // Inventory: enable the live inventory table in place.
        await S3Fidelity.UpdateInventory(s3, bucket, "ENABLED");
        await S3Fidelity.PollMetadata(
            async () => (await Get()).InventoryTableConfigurationResult?.ConfigurationState?.Value,
            s => s == "ENABLED",
            timeoutS: 60,
            intervalS: 5,
            description: "inventory table ConfigurationState to flip to ENABLED");

        // Annotation: flip it ENABLED in place. A target that quietly no-ops this update is
        // indistinguishable from a working one unless the state actually changes, so this needs a
        // real transition rather than DISABLED -> DISABLED — which means provisioning the IAM role
        // S3 requires (real AWS rejects ENABLED with InvalidRequest if Role is absent). The check
        // creates the role only to pass its ARN; the cloud models no IAM, so its ARN stands in.
        var roleArn = ctx.RoleArn(FidelityContext.UniqueName("sim2real-behav-s3meta-role-"));

        // The role's trust policy is eventually consistent from S3's point of view -- retry
        // through AccessDenied for a bounded window rather than assuming it's immediately assumable.
        await S3Fidelity.RetryUntilSuccess(() => S3Fidelity.UpdateAnnotationTable(s3, bucket, "ENABLED", roleArn), timeoutS: 90, intervalS: 5);

        var annotation = await S3Fidelity.PollMetadata(
            async () => (await Get()).AnnotationTableConfigurationResult,
            a => a?.ConfigurationState?.Value == "ENABLED",
            timeoutS: 60,
            intervalS: 5,
            description: "annotation table ConfigurationState to flip to ENABLED");
        Ensure(annotation!.ConfigurationState?.Value == "ENABLED", S3Fidelity.Repr(annotation));
        Ensure(annotation.Role == roleArn, S3Fidelity.Repr(annotation));

        await S3Fidelity.DeleteMetadataConfiguration(s3, bucket);
        var (code, message) = await S3Fidelity.ErrorPair(
            () => s3.GetBucketMetadataConfigurationAsync(new GetBucketMetadataConfigurationRequest { BucketName = bucket }),
            "expected a ClientError, but the call succeeded");
        Ensure(code == "MetadataConfigurationNotFound", code ?? "None");
        Ensure(message == "The metadata configuration was not found", message ?? "None");
    }
}
