// Translated from behavioral/s3/check_copy_object_metadata_copy.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.copy-object-metadata-copy</summary>
[Category("AwsEvaluationSuite")]
public sealed class CopyObjectMetadataCopy
{
    /// <summary>CopyObject with MetadataDirective=COPY preserves source metadata.</summary>
    [Test]
    [DisplayName("s3.copy-object-metadata-copy")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", "true");
        var put = new PutObjectRequest { BucketName = bucket, Key = "cp-src.txt", ContentBody = "data" };
        put.Metadata.Add("keep-me", "yes");
        await s3.PutObjectAsync(put);
        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            DestinationBucket = bucket,
            DestinationKey = "cp-dst.txt",
            SourceBucket = bucket,
            SourceKey = "cp-src.txt",
            MetadataDirective = S3MetadataDirective.COPY,
        });
        var expected = new Dictionary<string, string> { ["keep-me"] = "yes" };
        var headDst = S3Fidelity.UserMetadata((await s3.GetObjectMetadataAsync(bucket, "cp-dst.txt")).Metadata);
        Ensure(
            Py.Equal(headDst, expected),
            $"expected COPY to preserve exactly the source metadata, got {Py.Repr(headDst)}");
        var headSrc = S3Fidelity.UserMetadata((await s3.GetObjectMetadataAsync(bucket, "cp-src.txt")).Metadata);
        Ensure(
            Py.Equal(headSrc, expected),
            $"expected the source object's own metadata to be unaffected by the copy, got {Py.Repr(headSrc)}");
    }
}
