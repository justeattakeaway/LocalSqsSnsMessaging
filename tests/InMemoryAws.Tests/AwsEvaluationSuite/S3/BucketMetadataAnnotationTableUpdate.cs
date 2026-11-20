// Translated from behavioral/s3/check_bucket_metadata_annotation_table_update.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-metadata-annotation-table-update</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketMetadataAnnotationTableUpdate
{
    /// <summary>
    /// After a base S3 Metadata configuration exists, UpdateBucketMetadataAnnotationTableConfiguration
    /// modifies the annotation-table settings and GetBucketMetadataConfiguration reflects the update.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-metadata-annotation-table-update")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-metaann-");
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = bucket, TagSet = [new Tag { Key = "sim2real", Value = "true" }] });
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

        // A freshly created base configuration may already report a default annotation-table
        // entry (e.g. ConfigurationState DISABLED) rather than omitting the key entirely, so
        // capture whatever the current state actually is instead of assuming it is absent, and
        // drive the update to the opposite state so the effect of the update is observable.
        var beforeAnnotation = before.AnnotationTableConfigurationResult;
        var beforeState = beforeAnnotation is not null ? beforeAnnotation.ConfigurationState?.Value : "DISABLED";
        var targetState = beforeState != "ENABLED" ? "ENABLED" : "DISABLED";

        string? roleArn = null;
        if (targetState == "ENABLED")
        {
            // Real AWS rejects the update with InvalidRequest ("When AnnotationTableConfiguration
            // is set to ENABLED, Role must be specified. Specify a valid Role ARN, and then try
            // again.") unless a Role S3 can assume (to manage the underlying annotation table) is
            // supplied. The check creates an IAM role only to pass its ARN here; the cloud models
            // no IAM, so its ARN stands in.
            roleArn = ctx.RoleArn($"sim2real-metaann-{bucket[^24..]}");
        }

        // IAM role creation is eventually consistent from S3's point of view; retry a bounded
        // window on InvalidRequest rather than assuming the freshly created role is immediately
        // assumable.
        await S3Fidelity.RetryOnInvalidRequest(() => S3Fidelity.UpdateAnnotationTable(s3, bucket, targetState, roleArn), timeoutS: 90, intervalS: 5);

        var after = await S3Fidelity.PollMetadata(
            () => S3Fidelity.MetadataResult(s3, bucket),
            r => r.AnnotationTableConfigurationResult?.ConfigurationState?.Value == targetState,
            timeoutS: 60,
            intervalS: 3);
        var afterAnnotation = after.AnnotationTableConfigurationResult;
        Ensure(
            afterAnnotation.ConfigurationState?.Value == targetState,
            $"expected AnnotationTableConfigurationResult.ConfigurationState {Py.Repr(targetState)}, got {Py.Repr(afterAnnotation.ConfigurationState?.Value)}");
        if (targetState == "ENABLED")
        {
            Ensure(
                afterAnnotation.Role == roleArn,
                $"expected the update to echo back the supplied Role {Py.Repr(roleArn)}, got {Py.Repr(afterAnnotation.Role)}");
        }

        Ensure(targetState != beforeState, "update should actually change the annotation table configuration state");
    }
}
