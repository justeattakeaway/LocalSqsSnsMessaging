// Translated from behavioral/s3/check_copy_object_not_in_active_tier_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.copy-object-not-in-active-tier-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CopyObjectNotInActiveTierRejected
{
    /// <summary>
    /// CopyObject from an archived, unrestored GLACIER object fails with InvalidObjectState and its exact
    /// message and creates no destination, while the same copy from a STANDARD source is byte-identical.
    /// </summary>
    [Test]
    [DisplayName("s3.copy-object-not-in-active-tier-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        // Positive control: CopyObject from a STANDARD-tier source succeeds byte-for-byte.
        const string stdSrc = "standard-src.bin";
        var stdBody = System.Security.Cryptography.RandomNumberGenerator.GetBytes(1024);
        await S3Fidelity.PutWithClass(s3, name, stdSrc, stdBody);
        await Copy(s3, name, stdSrc, "dst-from-standard.bin");
        var copiedBody = await S3Fidelity.GetBytes(s3, name, "dst-from-standard.bin");
        Ensure(copiedBody.SequenceEqual(stdBody), "positive-control copy from a STANDARD source must be byte-identical");

        const string src = "archived-src.bin";
        await S3Fidelity.PutWithClass(s3, name, src, System.Security.Cryptography.RandomNumberGenerator.GetBytes(1024), "GLACIER");
        var refusal = await S3Fidelity.Refusal(() => Copy(s3, name, src, "dst.bin"));
        Ensure(refusal == CopyArchived, $"expected {S3Fidelity.Repr(CopyArchived)}, got {S3Fidelity.Repr(refusal)}");
        var dst = await S3Fidelity.Refusal(() => s3.GetObjectMetadataAsync(name, "dst.bin"));
        Ensure(dst == HeadAbsent, $"expected the copy destination absent {S3Fidelity.Repr(HeadAbsent)}, got {S3Fidelity.Repr(dst)}");
    }

    private static readonly (string? Code, string? Message) CopyArchived = ("InvalidObjectState", "Operation is not valid for the source object's storage class");
    private static readonly (string? Code, string? Message) HeadAbsent = ("404", "Not Found");

    private static Task<CopyObjectResponse> Copy(AmazonS3Client s3, string bucket, string sourceKey, string destinationKey, string? storageClass = null) =>
        s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = bucket,
            SourceKey = sourceKey,
            DestinationBucket = bucket,
            DestinationKey = destinationKey,
            StorageClass = storageClass is null ? null : new S3StorageClass(storageClass),
        });
}
