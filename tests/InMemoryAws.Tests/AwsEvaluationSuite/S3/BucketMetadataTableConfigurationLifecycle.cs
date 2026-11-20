// Translated from behavioral/s3/check_bucket_metadata_table_configuration_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-metadata-table-configuration-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketMetadataTableConfigurationLifecycle
{
    /// <summary>
    /// CreateBucketMetadataConfiguration is reflected by Get with the journal table's RecordExpiration
    /// DISABLED and TableStatus CREATING or ACTIVE, and after DeleteBucketMetadataConfiguration Get fails
    /// with MetadataConfigurationNotFound.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-metadata-table-configuration-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-metatbl-");
        ctx.OnCleanup(() => S3Fidelity.SafeDeleteMetadataConfiguration(s3, bucket));

        // S3 rejects the V1 *BucketMetadataTableConfiguration operations with V1APIsNotAllowed.
        if (await FidelityContext.CaptureError(() => S3Fidelity.CreateMetadataConfiguration(s3, bucket)) is { } error)
        {
            if (error.Code is "AccessDenied" or "AccessDeniedException")
            {
                throw Fail("insufficient permissions to create a metadata table configuration");
            }

            throw error.Exception;
        }

        // The table bucket and namespace S3 reports for the configuration, read before it is deleted
        // (the check's teardown deletes its tables through S3 Tables).
        DestinationResult? destination = null;
        var result = await S3Fidelity.PollMetadata(
            async () =>
            {
                var read = await S3Fidelity.MetadataResult(s3, bucket);
                destination = read.DestinationResult;
                return read;
            },
            r => r.JournalTableConfigurationResult is not null,
            timeoutS: 180,
            intervalS: 5);
        Ensure(destination?.TableBucketArn is not null, "the configuration names no destination table bucket");
        var journal = result.JournalTableConfigurationResult;
        Ensure(journal.RecordExpiration.Expiration?.Value == "DISABLED", "assert journal['RecordExpiration']['Expiration'] == 'DISABLED'");
        Ensure(journal.TableStatus is "CREATING" or "ACTIVE", journal.TableStatus ?? "None");

        await S3Fidelity.DeleteMetadataConfiguration(s3, bucket);

        // Retried because teardown is not always instantaneous; only the not-found code counts.
        var clock = FidelityTime.StartNew();
        string? code = null;
        var gone = false;
        while (clock.Elapsed.TotalSeconds < 240)
        {
            var failure = await FidelityContext.CaptureError(
                () => s3.GetBucketMetadataConfigurationAsync(new GetBucketMetadataConfigurationRequest { BucketName = bucket }));
            code = failure?.Code;
            if (code == "MetadataConfigurationNotFound")
            {
                gone = true;
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(5));
        }

        Ensure(gone, $"expected MetadataConfigurationNotFound after delete, got error code {Py.Repr(code)}");
    }
}
