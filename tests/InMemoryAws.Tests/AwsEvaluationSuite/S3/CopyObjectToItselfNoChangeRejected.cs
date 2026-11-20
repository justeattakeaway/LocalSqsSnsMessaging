// Translated from behavioral/s3/check_copy_object_to_itself_no_change_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.copy-object-to-itself-no-change-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CopyObjectToItselfNoChangeRejected
{
    private static readonly (string? Code, string? Message) SelfCopyRefused = (
        "InvalidRequest",
        "This copy request is illegal because it is trying to copy an object to itself without changing the object's metadata, storage class, website redirect location or encryption attributes.");

    /// <summary>Copying an object onto itself with MetadataDirective=COPY is a no-op request and must be rejected with InvalidRequest.</summary>
    [Test]
    [DisplayName("s3.copy-object-to-itself-no-change-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "self-copy.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "payload", ContentType = "text/plain" });

        var refusal = await S3Fidelity.ErrorPair(() => s3.CopyObjectAsync(new CopyObjectRequest
        {
            DestinationBucket = bucket,
            DestinationKey = key,
            SourceBucket = bucket,
            SourceKey = key,
            MetadataDirective = S3MetadataDirective.COPY,
        }));
        Ensure(
            refusal == SelfCopyRefused,
            $"expected {SelfCopyRefused} for a self-copy with no change, got {refusal}");

        // the same self-copy IS legal when metadata actually changes
        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            DestinationBucket = bucket,
            DestinationKey = key,
            SourceBucket = bucket,
            SourceKey = key,
            MetadataDirective = S3MetadataDirective.REPLACE,
            ContentType = "application/json",
        });
        var replacedType = (await s3.GetObjectMetadataAsync(bucket, key)).Headers.ContentType;
        Ensure(
            replacedType == "application/json",
            $"expected the metadata-changing self-copy to take effect, got ContentType={Py.Repr(replacedType)}");
    }
}
