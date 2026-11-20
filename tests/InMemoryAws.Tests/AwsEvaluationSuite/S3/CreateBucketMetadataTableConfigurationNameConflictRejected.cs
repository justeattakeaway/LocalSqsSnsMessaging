// Translated from behavioral/s3/check_create_bucket_metadata_table_configuration_name_conflict_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.create-bucket-metadata-table-configuration-name-conflict-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateBucketMetadataTableConfigurationNameConflictRejected
{
    private static readonly (string, string) AlreadyExists = ("MetadataConfigurationAlreadyExists", "The metadata configuration already exists");

    /// <summary>
    /// A second CreateBucketMetadataConfiguration on a bucket whose configuration is ACTIVE is rejected
    /// as MetadataConfigurationAlreadyExists rather than overwriting or duplicating the AWS-managed
    /// metadata tables; a re-create after delete fails on the leftover journal table.
    /// </summary>
    [Test]
    [DisplayName("s3.create-bucket-metadata-table-configuration-name-conflict-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-mtbc-");
        ctx.OnCleanup(() => DeleteMetadataConfiguration(s3, bucket));

        await S3Fidelity.CreateMetadataConfiguration(s3, bucket);
        var status = await PollJournalStatus(s3, bucket, timeoutS: 180.0);
        Ensure(status == "ACTIVE", $"expected first configuration ACTIVE, got {status}");

        // Creating a SECOND configuration on the SAME bucket while the first is still
        // active must be rejected as a conflict, not silently accepted/duplicated.
        var conflict = await S3Fidelity.ErrorPair(
            () => S3Fidelity.CreateMetadataConfiguration(s3, bucket),
            "expected a ClientError, but the call SUCCEEDED");
        Ensure(conflict == AlreadyExists, $"expected {S3Fidelity.Repr(AlreadyExists)}, got {S3Fidelity.Repr(conflict)}");

        // effect unchanged: the original configuration is still exactly the one in place
        var stillStatus = await S3Fidelity.JournalStatus(s3, bucket);
        Ensure(stillStatus == "ACTIVE", $"expected original configuration still ACTIVE, got {stillStatus}");

        // restore, and a deeper real invariant with it: deleting the metadata
        // CONFIGURATION does not delete the journal TABLE it provisioned in the
        // AWS-managed table bucket namespace. The identical create is accepted
        // synchronously (unlike the first conflict above, which botocore raises
        // immediately), but the async provisioning it kicks off still conflicts with
        // that leftover table and lands in FAILED with a table-scoped error -- distinct
        // from the configuration-scoped MetadataConfigurationAlreadyExists asserted
        // above. A target that treated "delete the configuration" as freeing the name
        // for immediate reuse would diverge from real AWS right here.
        await DeleteMetadataConfiguration(s3, bucket);
        await S3Fidelity.CreateMetadataConfiguration(s3, bucket);
        var (restoredStatus, restoredError) = await PollJournalStatusDetail(s3, bucket, timeoutS: 180.0);
        Ensure(
            restoredStatus == "FAILED",
            "expected the identical create to still conflict on the leftover journal " +
            $"table, got status={restoredStatus} error={S3Fidelity.Repr(restoredError)}");
        const string wantCode = "JournalTableAlreadyExists";
        const string wantMessage =
            "A journal table already exists in the AWS managed table bucket's namespace. " +
            "Delete the journal table, and then try again. To create a new S3 Metadata " +
            "table, you must delete the S3 Metadata configuration for this bucket, and " +
            "then create a new S3 Metadata configuration.";
        Ensure(
            restoredError?.ErrorCode == wantCode && restoredError.ErrorMessage == wantMessage,
            $"expected {{'ErrorCode': {Py.Repr(wantCode)}, 'ErrorMessage': {Py.Repr(wantMessage)}}} on the leftover journal table, got {S3Fidelity.Repr(restoredError)}");

        // positive control: a metadata configuration on a fresh, unrelated bucket succeeds
        var controlBucket = await S3Fidelity.CreateBucket(ctx, "sim2real-mtbc-ctrl-");
        await S3Fidelity.CreateMetadataConfiguration(s3, controlBucket);
        ctx.OnCleanup(() => DeleteMetadataConfiguration(s3, controlBucket));
        var controlStatus = await PollJournalStatus(s3, controlBucket, timeoutS: 180.0);
        Ensure(controlStatus == "ACTIVE", $"expected control configuration ACTIVE, got {controlStatus}");
    }

    private static async Task<string?> PollJournalStatus(IAmazonS3 s3, string bucket, double timeoutS = 180.0, double intervalS = 5.0) =>
        (await PollJournalStatusDetail(s3, bucket, timeoutS, intervalS)).Status;

    private static async Task<(string? Status, ErrorDetails? Error)> PollJournalStatusDetail(IAmazonS3 s3, string bucket, double timeoutS = 180.0, double intervalS = 5.0)
    {
        var clock = FidelityTime.StartNew();
        string? status = null;
        ErrorDetails? error = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            var journal = (await S3Fidelity.MetadataResult(s3, bucket)).JournalTableConfigurationResult;
            status = journal.TableStatus;
            error = journal.Error;
            if (status is "ACTIVE" or "FAILED")
            {
                return (status, error);
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        return (status, error);
    }

    /// <summary>Delete the bucket's metadata configuration; absent is done, a 409 conflict is retried.</summary>
    private static async Task DeleteMetadataConfiguration(IAmazonS3 s3, string bucket)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => S3Fidelity.DeleteMetadataConfiguration(s3, bucket));
            if (error is null || error.Code is "MetadataConfigurationNotFound" or "NoSuchBucket" || (int)error.StatusCode == 404)
            {
                return;
            }

            if ((int)error.StatusCode != 409 || clock.Elapsed.TotalSeconds >= 60)
            {
                throw error.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }
}
