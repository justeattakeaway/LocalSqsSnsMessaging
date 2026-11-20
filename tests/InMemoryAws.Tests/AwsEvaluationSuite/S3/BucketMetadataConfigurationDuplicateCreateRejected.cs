// Translated from behavioral/s3/check_bucket_metadata_configuration_duplicate_create_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-metadata-configuration-duplicate-create-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketMetadataConfigurationDuplicateCreateRejected
{
    /// <summary>
    /// A second CreateBucketMetadataConfiguration on a bucket that already has one is rejected
    /// (state-conflict), leaving the original configuration intact.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-metadata-configuration-duplicate-create-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-metadup-");

        if (await FidelityContext.CaptureError(() => S3Fidelity.CreateMetadataConfiguration(s3, bucket)) is { } error)
        {
            if (error.Code is "AccessDenied" or "AccessDeniedException")
            {
                throw Fail($"insufficient permissions for S3 Metadata configuration: {error.Code}");
            }

            throw error.Exception;
        }

        ctx.OnCleanup(() => S3Fidelity.SafeDeleteMetadataConfiguration(s3, bucket));

        var (code, message) = await S3Fidelity.ErrorPair(() => S3Fidelity.CreateMetadataConfiguration(s3, bucket));
        Ensure(code == "MetadataConfigurationAlreadyExists", $"unexpected code for duplicate create: {code}");
        Ensure(message == "The metadata configuration already exists", message ?? "None");

        // original configuration must still be present/unaffected by the rejected duplicate
        var got = await s3.GetBucketMetadataConfigurationAsync(new GetBucketMetadataConfigurationRequest { BucketName = bucket });
        Ensure(got.GetBucketMetadataConfigurationResult is not null, "assert 'GetBucketMetadataConfigurationResult' in got");
        Ensure(got.GetBucketMetadataConfigurationResult.MetadataConfigurationResult.JournalTableConfigurationResult is not null, "assert 'JournalTableConfigurationResult' in meta_result");

        // restore leg: the rejection is a state conflict, not a malformed request, so once the
        // existing configuration is removed the identical call must succeed again.
        await S3Fidelity.DeleteMetadataConfiguration(s3, bucket);
        var (goneCode, goneMessage) = await S3Fidelity.ErrorPair(
            () => s3.GetBucketMetadataConfigurationAsync(new GetBucketMetadataConfigurationRequest { BucketName = bucket }));
        Ensure(goneCode == "MetadataConfigurationNotFound", $"expected MetadataConfigurationNotFound after delete, got {goneCode}");
        Ensure(goneMessage == "The metadata configuration was not found", goneMessage ?? "None");

        await S3Fidelity.CreateMetadataConfiguration(s3, bucket);
        var restored = await S3Fidelity.MetadataResult(s3, bucket);
        Ensure(
            restored.JournalTableConfigurationResult is not null,
            "expected the identical create to succeed once the state conflict is cleared");
    }
}
