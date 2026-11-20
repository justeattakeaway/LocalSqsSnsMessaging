// Translated from behavioral/s3/check_metadata_table_configuration_invalid_schema_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.metadata-table-configuration-invalid-schema-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class MetadataTableConfigurationInvalidSchemaRejected
{
    // NOTE on the pinned error code: per API_RecordExpiration.html, when journal table record
    // expiration is ENABLED, Days must be a whole number from 7 to 2147483647 ("Journal table records
    // must be retained for a minimum of 7 days"). Days=1 violates that documented constraint, so the
    // service must reject the request synchronously with InvalidRequest rather than silently
    // clamping or accepting it.

    /// <summary>
    /// CreateBucketMetadataConfiguration with RecordExpiration.Days below the 7-day minimum is rejected
    /// with InvalidRequest and its exact message and leaves no configuration (MetadataConfigurationNotFound),
    /// while a valid configuration is then created and reaches ACTIVE.
    /// </summary>
    [Test]
    [DisplayName("s3.metadata-table-configuration-invalid-schema-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");

        var (code, message) = await S3Fidelity.ErrorPair(
            () => S3Fidelity.CreateMetadataConfiguration(s3, bucket, "ENABLED", 1),
            "expected a ClientError, but the call succeeded");
        Ensure(code == "InvalidRequest", code ?? "None");
        Ensure(
            message ==
                "When RecordExpiration is set to ENABLED, the Days value must be specified. " +
                "This value must be a whole number from 7 to 2147483647. " +
                "Specify a valid Days value, and then try again.",
            message ?? "None");

        // Effect absent: the rejected attempt left no configuration behind.
        var (absentCode, absentMessage) = await S3Fidelity.ErrorPair(
            () => s3.GetBucketMetadataConfigurationAsync(new GetBucketMetadataConfigurationRequest { BucketName = bucket }),
            "expected a ClientError, but the call succeeded");
        Ensure(absentCode == "MetadataConfigurationNotFound", absentCode ?? "None");
        Ensure(absentMessage == "The metadata configuration was not found", absentMessage ?? "None");

        // Positive control: a valid RecordExpiration is accepted.
        await S3Fidelity.CreateMetadataConfigurationOrFail(s3, bucket);
        ctx.OnCleanup(() => S3Fidelity.SafeDeleteMetadataConfiguration(s3, bucket));

        var journal = await PollJournalTerminal(s3, bucket);
        Ensure(journal.TableStatus == "ACTIVE", S3Fidelity.Repr(journal));
    }

    /// <summary>Poll the journal table until it leaves CREATING; raise on timeout.</summary>
    private static async Task<JournalTableConfigurationResult> PollJournalTerminal(IAmazonS3 s3, string bucket, double timeout = 300.0, double interval = 10.0)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var journal = (await S3Fidelity.MetadataResult(s3, bucket)).JournalTableConfigurationResult;
            if (journal.TableStatus != "CREATING")
            {
                return journal;
            }

            if (clock.Elapsed.TotalSeconds >= timeout)
            {
                throw Fail($"journal table still CREATING after {timeout:0}s: {S3Fidelity.Repr(journal)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(interval));
        }
    }
}
