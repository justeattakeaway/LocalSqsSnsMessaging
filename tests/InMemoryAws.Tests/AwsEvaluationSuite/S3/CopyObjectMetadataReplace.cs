// Translated from behavioral/s3/check_copy_object_metadata_replace.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.copy-object-metadata-replace</summary>
[Category("AwsEvaluationSuite")]
public sealed class CopyObjectMetadataReplace
{
    /// <summary>CopyObject with MetadataDirective=REPLACE uses new metadata.</summary>
    [Test]
    [DisplayName("s3.copy-object-metadata-replace")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var put = new PutObjectRequest { BucketName = bucket, Key = "meta-src.txt", ContentBody = "data", ContentType = "text/plain" };
        put.Metadata.Add("original", "true");
        await s3.PutObjectAsync(put);
        var copy = new CopyObjectRequest
        {
            DestinationBucket = bucket,
            DestinationKey = "meta-dst.txt",
            SourceBucket = bucket,
            SourceKey = "meta-src.txt",
            MetadataDirective = S3MetadataDirective.REPLACE,
            ContentType = "application/octet-stream",
        };
        copy.Metadata.Add("replaced", "yes");
        await s3.CopyObjectAsync(copy);
        var head = await s3.GetObjectMetadataAsync(bucket, "meta-dst.txt");
        var metadata = S3Fidelity.UserMetadata(head.Metadata);
        Ensure(
            metadata.GetValueOrDefault("replaced") == "yes",
            $"expected replaced metadata 'yes', got {Py.Repr(metadata.GetValueOrDefault("replaced"))}");
        Ensure(
            !metadata.ContainsKey("original"),
            $"expected source metadata dropped under REPLACE, still present: {Py.Repr(metadata)}");
        Ensure(
            head.Headers.ContentType == "application/octet-stream",
            $"expected replaced content-type 'application/octet-stream', got {Py.Repr(head.Headers.ContentType)}");
    }
}
