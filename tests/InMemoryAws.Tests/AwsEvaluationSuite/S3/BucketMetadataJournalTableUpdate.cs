// Translated from behavioral/s3/check_bucket_metadata_journal_table_update.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-metadata-journal-table-update</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketMetadataJournalTableUpdate
{
    /// <summary>
    /// After a base S3 Metadata configuration exists, UpdateBucketMetadataJournalTableConfiguration
    /// modifies journal-table record expiration and GetBucketMetadataConfiguration reflects the update.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-metadata-journal-table-update")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-metajrn-");
        ctx.OnCleanup(() => S3Fidelity.SafeDeleteMetadataConfiguration(s3, bucket));

        if (await FidelityContext.CaptureError(() => S3Fidelity.CreateMetadataConfiguration(s3, bucket)) is { } error)
        {
            if (error.Code is "AccessDenied" or "AccessDeniedException")
            {
                throw Fail("insufficient permissions to create S3 Metadata configuration");
            }

            throw error.Exception;
        }

        var before = await S3Fidelity.PollMetadata(
            () => S3Fidelity.MetadataResult(s3, bucket),
            r => r.JournalTableConfigurationResult is not null,
            timeoutS: 60,
            intervalS: 3);
        var beforeJournal = before.JournalTableConfigurationResult;
        Ensure(
            beforeJournal.RecordExpiration.Expiration?.Value == "DISABLED",
            $"expected freshly created journal table to report RecordExpiration DISABLED, got {S3Fidelity.Repr(beforeJournal.RecordExpiration)}");

        // Immediately after CreateBucketMetadataConfiguration, the journal table can still be
        // settling; a mutation right away may transiently fail with InvalidRequest. Retry the
        // update within a bounded window rather than assuming it always succeeds on the first try.
        await S3Fidelity.RetryOnInvalidRequest(() => S3Fidelity.UpdateJournal(s3, bucket, "ENABLED", 30), timeoutS: 60, intervalS: 3);

        var after = await S3Fidelity.PollMetadata(
            () => S3Fidelity.MetadataResult(s3, bucket),
            r => r.JournalTableConfigurationResult.RecordExpiration.Expiration?.Value == "ENABLED",
            timeoutS: 60,
            intervalS: 3);
        var afterJournal = after.JournalTableConfigurationResult;
        Ensure(
            afterJournal.RecordExpiration.Expiration?.Value == "ENABLED",
            "expected UpdateBucketMetadataJournalTableConfiguration to flip Expiration to ENABLED, " +
            $"GetBucketMetadataConfiguration still reports {S3Fidelity.Repr(afterJournal.RecordExpiration)}");
        Ensure(
            afterJournal.RecordExpiration.Days == 30,
            $"expected the updated journal table RecordExpiration.Days to be 30, got {Py.Repr(afterJournal.RecordExpiration.Days)}");
    }
}
