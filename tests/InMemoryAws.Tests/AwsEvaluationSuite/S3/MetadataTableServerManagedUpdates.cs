// Translated from behavioral/s3/check_metadata_table_server_managed_updates.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.metadata-table-server-managed-updates</summary>
[Category("AwsEvaluationSuite")]
public sealed class MetadataTableServerManagedUpdates
{
    /// <summary>
    /// Each UpdateBucketMetadata*TableConfiguration call changes only its own sub-resource:
    /// GetBucketMetadataConfiguration shows the new value while sibling sub-resources set by earlier
    /// calls stay intact.
    /// </summary>
    [Test]
    [DisplayName("s3.metadata-table-server-managed-updates")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");

        if (await FidelityContext.CaptureError(() => S3Fidelity.CreateMetadataConfiguration(s3, bucket, inventory: "DISABLED")) is { } error)
        {
            throw Fail($"prerequisite S3 Metadata (table buckets) unavailable in this account/region: {error.Code}");
        }

        ctx.OnCleanup(() => S3Fidelity.SafeDeleteMetadataConfiguration(s3, bucket));

        // GetBucketMetadataConfiguration is eventually consistent after creation: the tables are
        // provisioned asynchronously, so reads can 404 for over a minute.
        var baseline = await PollUntilAvailable(() => S3Fidelity.MetadataResult(s3, bucket), timeoutS: 300, intervalS: 5.0);
        var baselineJournalExpiration = baseline.JournalTableConfigurationResult.RecordExpiration.Expiration?.Value;
        Ensure(
            baselineJournalExpiration == "DISABLED",
            $"expected freshly created journal table RecordExpiration DISABLED, got {Py.Repr(baselineJournalExpiration)}");
        var baselineInventoryState = baseline.InventoryTableConfigurationResult.ConfigurationState?.Value;
        Ensure(
            baselineInventoryState == "DISABLED",
            $"expected freshly created inventory table ConfigurationState DISABLED, got {Py.Repr(baselineInventoryState)}");

        // While the journal table is still CREATING, Update*JournalTableConfiguration fails with
        // InvalidRequest, so wait for ACTIVE first.
        await S3Fidelity.PollMetadata(
            () => S3Fidelity.MetadataResult(s3, bucket),
            r => r.JournalTableConfigurationResult.TableStatus == "ACTIVE",
            timeoutS: 300,
            intervalS: 5.0);

        await S3Fidelity.UpdateJournal(s3, bucket, "ENABLED", 7);
        var afterJournal = await S3Fidelity.PollMetadata(
            () => S3Fidelity.MetadataResult(s3, bucket),
            r => r.JournalTableConfigurationResult.RecordExpiration.Expiration?.Value == "ENABLED",
            timeoutS: 30,
            intervalS: 3.0);
        var journalResult = afterJournal.JournalTableConfigurationResult;
        Ensure(
            journalResult.RecordExpiration.Days == 7,
            $"expected updated journal RecordExpiration.Days == 7, got {Py.Repr(journalResult.RecordExpiration.Days)}");
        var journalStatus = journalResult.TableStatus;
        Ensure(journalStatus != "FAILED", $"expected journal TableStatus not FAILED after update, got {Py.Repr(journalStatus)}");

        // the untouched inventory sub-resource must still read back exactly as configured.
        var inventoryAfterJournalUpdate = afterJournal.InventoryTableConfigurationResult.ConfigurationState?.Value;
        Ensure(
            inventoryAfterJournalUpdate == "DISABLED",
            "journal update must not clobber the untouched inventory sub-resource: expected " +
            $"ConfigurationState DISABLED, got {Py.Repr(inventoryAfterJournalUpdate)}");

        await S3Fidelity.UpdateInventory(s3, bucket, "ENABLED");
        var afterInventory = await S3Fidelity.PollMetadata(
            () => S3Fidelity.MetadataResult(s3, bucket),
            r => r.InventoryTableConfigurationResult.ConfigurationState?.Value == "ENABLED",
            timeoutS: 60,
            intervalS: 3.0);
        var invResult = afterInventory.InventoryTableConfigurationResult;
        var inventoryStatus = invResult.TableStatus;
        Ensure(inventoryStatus != "FAILED", $"expected inventory TableStatus not FAILED after update, got {Py.Repr(inventoryStatus)}");
        var inventoryTableName = invResult.TableName;
        Ensure(
            !string.IsNullOrEmpty(inventoryTableName),
            $"expected a non-empty TableName for the enabled live inventory table, got {Py.Repr(inventoryTableName)}");

        // the journal sub-resource set a moment ago must remain intact: enabling inventory must
        // not have reset or corrupted the sibling journal configuration.
        var stillJournal = afterInventory.JournalTableConfigurationResult.RecordExpiration;
        Ensure(
            stillJournal.Expiration?.Value == "ENABLED" && stillJournal.Days == 7,
            "inventory update must not clobber the sibling journal sub-resource: expected " +
            $"Expiration ENABLED / Days 7, got {S3Fidelity.Repr(stillJournal)}");

        // The check creates an IAM role S3 assumes for the annotation table, only to pass its ARN;
        // the cloud models no IAM, so its ARN stands in.
        var roleArn = ctx.RoleArn(FidelityContext.UniqueName("sim2real-s3meta-role-"));

        // IAM role trust policy propagation is eventually consistent -- retry through AccessDenied.
        await S3Fidelity.RetryUntilSuccess(() => S3Fidelity.UpdateAnnotationTable(s3, bucket, "ENABLED", roleArn), timeoutS: 90, intervalS: 5);

        var afterAnnotation = await S3Fidelity.PollMetadata(
            () => S3Fidelity.MetadataResult(s3, bucket),
            r => r.AnnotationTableConfigurationResult is not null,
            timeoutS: 60,
            intervalS: 3.0);
        var annResult = afterAnnotation.AnnotationTableConfigurationResult;
        Ensure(
            annResult.ConfigurationState?.Value == "ENABLED",
            $"expected annotation ConfigurationState ENABLED after update, got {Py.Repr(annResult.ConfigurationState?.Value)}");
        Ensure(annResult.Role == roleArn, $"expected annotation Role {Py.Repr(roleArn)}, got {Py.Repr(annResult.Role)}");

        // the two earlier updates (journal, inventory) must still be intact after the third
        // server-managed mutation -- no intermediate-state corruption across the three sub-resources.
        var finalJournal = afterAnnotation.JournalTableConfigurationResult.RecordExpiration;
        Ensure(
            finalJournal.Expiration?.Value == "ENABLED" && finalJournal.Days == 7,
            "annotation update must not clobber the journal sub-resource: expected Expiration ENABLED / " +
            $"Days 7, got {S3Fidelity.Repr(finalJournal)}");
        var finalInventory = afterAnnotation.InventoryTableConfigurationResult.ConfigurationState?.Value;
        Ensure(
            finalInventory == "ENABLED",
            $"annotation update must not clobber the inventory sub-resource: expected ConfigurationState ENABLED, got {Py.Repr(finalInventory)}");
    }

    /// <summary>
    /// Like PollMetadata, but treats an error from the probe itself (e.g. the metadata configuration
    /// not yet being readable right after creation) as "not ready yet" and retries.
    /// </summary>
    private static async Task<T> PollUntilAvailable<T>(Func<Task<T>> probe, double timeoutS, double intervalS = 3.0)
        where T : class
    {
        var clock = FidelityTime.StartNew();
        AwsError? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            var (result, error) = await FidelityContext.Capture(probe);
            if (error is null)
            {
                return result!;
            }

            last = error;
            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"metadata configuration never became readable within {timeoutS}s: {last}");
    }
}
