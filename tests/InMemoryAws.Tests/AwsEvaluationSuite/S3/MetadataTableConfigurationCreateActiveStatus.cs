// Translated from behavioral/s3/check_metadata_table_configuration_create_active_status.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.metadata-table-configuration-create-active-status</summary>
[Category("AwsEvaluationSuite")]
public sealed class MetadataTableConfigurationCreateActiveStatus
{
    /// <summary>
    /// A newly created S3 Metadata (V2) configuration's journal table transitions from CREATING to
    /// ACTIVE, observable via subsequent GetBucketMetadataConfiguration calls.
    /// </summary>
    [Test]
    [DisplayName("s3.metadata-table-configuration-create-active-status")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");
        await S3Fidelity.CreateMetadataConfigurationOrFail(s3, bucket);
        ctx.OnCleanup(() => S3Fidelity.SafeDeleteMetadataConfiguration(s3, bucket));

        var result = await S3Fidelity.PollJournalUntil(s3, bucket, ["ACTIVE", "FAILED"], timeoutS: 180.0, intervalS: 5.0);
        if (result.TableStatus == "FAILED")
        {
            throw Fail($"journal table reached FAILED instead of ACTIVE: {S3Fidelity.Repr(result.Error)}");
        }

        Ensure(result.TableStatus == "ACTIVE", $"expected journal table status ACTIVE, got {Py.Repr(result.TableStatus)}");
        Ensure(!string.IsNullOrEmpty(result.TableName), $"expected a non-empty TableName, got {Py.Repr(result.TableName)}");
        Ensure(!string.IsNullOrEmpty(result.TableArn), $"expected a non-empty TableArn, got {Py.Repr(result.TableArn)}");
    }
}
