// Translated from behavioral/s3/check_bucket_metadata_configuration_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-metadata-configuration-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketMetadataConfigurationRoundtrip
{
    /// <summary>
    /// CreateBucketMetadataConfiguration is retrievable and removable via
    /// DeleteBucketMetadataConfiguration.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-metadata-configuration-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await S3Fidelity.CreateMetadataConfiguration(s3, bucket);

        var fetched = await S3Fidelity.PollMetadata(
            () => S3Fidelity.MetadataResult(s3, bucket),
            r => r.JournalTableConfigurationResult?.TableStatus == "ACTIVE",
            timeoutS: 120,
            intervalS: 5,
            description: "journal table to become ACTIVE");
        var journal = fetched.JournalTableConfigurationResult;
        Ensure(
            journal.RecordExpiration.Expiration?.Value == "DISABLED",
            "expected the RecordExpiration set at create time to round-trip through Get, " +
            $"got {S3Fidelity.Repr(journal.RecordExpiration)}");

        await S3Fidelity.DeleteMetadataConfiguration(s3, bucket);
        var (code, message) = await S3Fidelity.ErrorPair(
            () => s3.GetBucketMetadataConfigurationAsync(new GetBucketMetadataConfigurationRequest { BucketName = bucket }),
            "expected a ClientError, but the call succeeded");
        Ensure(
            code == "MetadataConfigurationNotFound",
            $"expected MetadataConfigurationNotFound for GetBucketMetadataConfiguration after delete, got {Py.Repr(code)}");
        Ensure(message == "The metadata configuration was not found", message ?? "None");
    }
}
