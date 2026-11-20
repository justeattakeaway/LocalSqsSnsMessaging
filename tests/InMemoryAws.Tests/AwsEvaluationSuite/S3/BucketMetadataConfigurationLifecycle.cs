// Translated from behavioral/s3/check_bucket_metadata_configuration_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-metadata-configuration-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketMetadataConfigurationLifecycle
{
    /// <summary>CreateBucketMetadataConfiguration provisions the config, Get reflects it, Delete removes it.</summary>
    [Test]
    [DisplayName("s3.bucket-metadata-configuration-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-metacfg-");
        ctx.OnCleanup(() => S3Fidelity.SafeDeleteMetadataConfiguration(s3, bucket));

        if (await FidelityContext.CaptureError(() => S3Fidelity.CreateMetadataConfiguration(s3, bucket)) is { } error)
        {
            if (error.Code is "AccessDenied" or "AccessDeniedException")
            {
                throw Fail("insufficient permissions to create S3 Metadata configuration");
            }

            throw error.Exception;
        }

        var result = await S3Fidelity.PollMetadata(
            () => S3Fidelity.MetadataResult(s3, bucket),
            r => r.JournalTableConfigurationResult is not null,
            timeoutS: 60,
            intervalS: 3);
        var journal = result.JournalTableConfigurationResult;
        Ensure(
            journal.RecordExpiration.Expiration?.Value == "DISABLED",
            $"expected journal table RecordExpiration to reflect the DISABLED value we set, got {S3Fidelity.Repr(journal.RecordExpiration)}");
        Ensure(
            journal.TableStatus is "CREATING" or "ACTIVE" or null,
            $"expected journal table status to be CREATING, ACTIVE or absent shortly after creation, got {Py.Repr(journal.TableStatus)}");

        await S3Fidelity.DeleteMetadataConfiguration(s3, bucket);

        // Deletion is asynchronous on real AWS: GetBucketMetadataConfiguration can keep
        // succeeding for a while after the delete call returns (reflecting a configuration that
        // no longer includes the journal table) before it eventually starts raising a
        // "no such configuration" style error. Accept either signal that the configuration is
        // gone, and give the async teardown a generous bounded window.
        var clock = FidelityTime.StartNew();
        string? code = null;
        var gone = false;
        while (clock.Elapsed.TotalSeconds < 240)
        {
            var (response, failure) = await FidelityContext.Capture(
                () => s3.GetBucketMetadataConfigurationAsync(new GetBucketMetadataConfigurationRequest { BucketName = bucket }));
            if (failure is not null)
            {
                code = failure.Code;
                if (code is "InvalidRequest" or "NoSuchConfiguration" or "MetadataConfigurationNotFound")
                {
                    gone = true;
                    break;
                }
            }
            else
            {
                code = null;
                if (response!.GetBucketMetadataConfigurationResult?.MetadataConfigurationResult?.JournalTableConfigurationResult is null)
                {
                    gone = true;
                    break;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(5));
        }

        Ensure(gone, $"expected metadata configuration to be absent after delete, got error code {Py.Repr(code)}");
    }
}
