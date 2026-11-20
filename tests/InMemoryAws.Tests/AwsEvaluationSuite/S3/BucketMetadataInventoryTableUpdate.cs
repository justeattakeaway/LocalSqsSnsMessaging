// Translated from behavioral/s3/check_bucket_metadata_inventory_table_update.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-metadata-inventory-table-update</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketMetadataInventoryTableUpdate
{
    /// <summary>
    /// After a base S3 Metadata configuration exists, UpdateBucketMetadataInventoryTableConfiguration
    /// enables the (auto-managed) live inventory table and GetBucketMetadataConfiguration reflects it.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-metadata-inventory-table-update")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-metainv-");
        ctx.OnCleanup(() => S3Fidelity.SafeDeleteMetadataConfiguration(s3, bucket));

        if (await FidelityContext.CaptureError(() => S3Fidelity.CreateMetadataConfiguration(s3, bucket)) is { } error)
        {
            if (error.Code is "AccessDenied" or "AccessDeniedException")
            {
                throw Fail("insufficient permissions to create S3 Metadata configuration");
            }

            throw error.Exception;
        }

        // Real AWS rejects UpdateBucketMetadataInventoryTableConfiguration with InvalidRequest
        // ("You can't perform this operation while the journal table is being created. Wait for
        // your journal table status to transition to ACTIVE, and then try again.") while the base
        // journal table is still CREATING, so wait for it to actually reach ACTIVE (not merely be
        // present) before attempting the inventory-table update.
        var before = await S3Fidelity.PollMetadata(
            () => S3Fidelity.MetadataResult(s3, bucket),
            r => r.JournalTableConfigurationResult?.TableStatus == "ACTIVE",
            timeoutS: 180,
            intervalS: 5);

        // A freshly created base configuration may already report a default (e.g. DISABLED)
        // inventory-table entry rather than omitting the key entirely, so only require that the
        // live inventory table isn't already enabled -- the update below is what enables it.
        var beforeInventory = before.InventoryTableConfigurationResult;
        Ensure(
            beforeInventory is null || beforeInventory.ConfigurationState?.Value != "ENABLED",
            "expected inventory table not yet ENABLED on a fresh base configuration, " +
            $"got InventoryTableConfigurationResult={S3Fidelity.Repr(beforeInventory)}");

        if (await FidelityContext.CaptureError(
                () => S3Fidelity.RetryOnInvalidRequest(() => S3Fidelity.UpdateInventory(s3, bucket, "ENABLED"), timeoutS: 60, intervalS: 3)) is { } updateError)
        {
            if (updateError.Code is "AccessDenied" or "AccessDeniedException")
            {
                throw Fail("insufficient permissions to enable a live inventory table");
            }

            throw updateError.Exception;
        }

        var after = await S3Fidelity.PollMetadata(
            () => S3Fidelity.MetadataResult(s3, bucket),
            r => r.InventoryTableConfigurationResult is not null,
            timeoutS: 60,
            intervalS: 3);
        var inv = after.InventoryTableConfigurationResult;
        Ensure(
            inv.ConfigurationState?.Value == "ENABLED",
            $"expected ConfigurationState ENABLED after the update, got {Py.Repr(inv.ConfigurationState?.Value)}");
        Ensure(
            inv.TableStatus is "CREATING" or "ACTIVE" or null,
            "expected the newly-enabled inventory table to be settling (CREATING/ACTIVE/absent), " +
            $"not a terminal failure state, got TableStatus={Py.Repr(inv.TableStatus)}");
    }
}
