// Translated from behavioral/s3/check_metadata_table_configuration_get_without_create_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.metadata-table-configuration-get-without-create-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class MetadataTableConfigurationGetWithoutCreateRejected
{
    /// <summary>
    /// GetBucketMetadataConfiguration on a bucket that never had one fails with
    /// MetadataConfigurationNotFound and its exact message instead of an empty 200, and succeeds once a
    /// configuration is created and its journal table is ACTIVE.
    /// </summary>
    [Test]
    [DisplayName("s3.metadata-table-configuration-get-without-create-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");

        var (code, message) = await S3Fidelity.ErrorPair(
            () => s3.GetBucketMetadataConfigurationAsync(new GetBucketMetadataConfigurationRequest { BucketName = bucket }));
        Ensure(code == "MetadataConfigurationNotFound", $"expected MetadataConfigurationNotFound, got {code}");
        Ensure(message == "The metadata configuration was not found", message ?? "None");

        // Positive control: after actually creating a configuration, Get succeeds and reflects it.
        await S3Fidelity.CreateMetadataConfigurationOrFail(s3, bucket);
        ctx.OnCleanup(() => S3Fidelity.SafeDeleteMetadataConfiguration(s3, bucket));

        var journal = await S3Fidelity.PollJournalUntil(s3, bucket, ["ACTIVE", "FAILED"], timeoutS: 180.0, intervalS: 5.0);
        Ensure(journal.TableStatus == "ACTIVE", $"journal table reached FAILED instead of ACTIVE: {S3Fidelity.Repr(journal.Error)}");
        Ensure(!string.IsNullOrEmpty(journal.TableName), "ACTIVE journal table result carried no TableName");
    }
}
