// Translated from behavioral/s3/check_metadata_table_configuration_delete_removes_table.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.metadata-table-configuration-delete-removes-table</summary>
[Category("AwsEvaluationSuite")]
public sealed class MetadataTableConfigurationDeleteRemovesTable
{
    /// <summary>
    /// After DeleteBucketMetadataConfiguration, Get and journal-table updates fail with
    /// MetadataConfigurationNotFound; a fresh configuration can then be created and read again.
    /// </summary>
    [Test]
    [DisplayName("s3.metadata-table-configuration-delete-removes-table")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");
        ctx.OnCleanup(() => S3Fidelity.SafeDeleteMetadataConfiguration(s3, bucket));

        await S3Fidelity.CreateMetadataConfigurationOrFail(s3, bucket);

        // Positive control: the configuration exists and is retrievable right after creation.
        var journal = (await S3Fidelity.MetadataResult(s3, bucket)).JournalTableConfigurationResult;
        Ensure(journal.TableStatus is "CREATING" or "ACTIVE", $"journal TableStatus {Py.Repr(journal.TableStatus)}");
        Ensure(
            journal.RecordExpiration.Expiration?.Value == "DISABLED",
            $"journal RecordExpiration {S3Fidelity.Repr(journal.RecordExpiration)}, expected the DISABLED value created");

        await S3Fidelity.DeleteMetadataConfiguration(s3, bucket);

        var (code, message) = await S3Fidelity.ErrorPair(
            () => s3.GetBucketMetadataConfigurationAsync(new GetBucketMetadataConfigurationRequest { BucketName = bucket }));
        Ensure(code == "MetadataConfigurationNotFound", code ?? "None");
        Ensure(message == "The metadata configuration was not found", message ?? "None");

        // A mutation on the now-dead configuration fails the same way rather than silently
        // reviving or no-op'ing it.
        var (mutateCode, mutateMessage) = await S3Fidelity.ErrorPair(() => S3Fidelity.UpdateJournal(s3, bucket, "ENABLED", 30));
        Ensure(mutateCode == "MetadataConfigurationNotFound", mutateCode ?? "None");
        Ensure(mutateMessage == "The metadata configuration was not found", mutateMessage ?? "None");

        // The bucket is reusable: a fresh configuration can be created and retrieved again.
        await S3Fidelity.CreateMetadataConfigurationOrFail(s3, bucket);
        var afterJournal = (await S3Fidelity.MetadataResult(s3, bucket)).JournalTableConfigurationResult;
        Ensure(afterJournal.TableStatus is "CREATING" or "ACTIVE", $"recreated journal TableStatus {Py.Repr(afterJournal.TableStatus)}");
        Ensure(
            afterJournal.RecordExpiration.Expiration?.Value == "DISABLED",
            $"recreated journal RecordExpiration {S3Fidelity.Repr(afterJournal.RecordExpiration)}, expected the DISABLED value created");
    }
}
