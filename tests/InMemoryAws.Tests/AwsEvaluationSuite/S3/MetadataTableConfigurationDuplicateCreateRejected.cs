// Translated from behavioral/s3/check_metadata_table_configuration_duplicate_create_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.metadata-table-configuration-duplicate-create-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class MetadataTableConfigurationDuplicateCreateRejected
{
    /// <summary>
    /// A second CreateBucketMetadataConfiguration fails with MetadataConfigurationAlreadyExists; the
    /// original stays ACTIVE on the same table, and object put/get still works.
    /// </summary>
    [Test]
    [DisplayName("s3.metadata-table-configuration-duplicate-create-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-metatbl-dup-");
        await S3Fidelity.CreateMetadataConfiguration(s3, bucket);
        ctx.OnCleanup(() => S3Fidelity.DeleteMetadataConfiguration(s3, bucket));
        await PollMetadataTableActive(s3, bucket);
        var originalTableArn = await JournalTableArn(s3, bucket);

        var (code, message) = await S3Fidelity.ErrorPair(
            () => S3Fidelity.CreateMetadataConfiguration(s3, bucket, "ENABLED", 30),
            "expected the duplicate CreateBucketMetadataConfiguration to fail, but it SUCCEEDED");
        Ensure(code == "MetadataConfigurationAlreadyExists", $"expected MetadataConfigurationAlreadyExists, got {code}");
        Ensure(message == "The metadata configuration already exists", message ?? "None");

        // Effect absent: still ACTIVE on the original journal table.
        await PollMetadataTableActive(s3, bucket);
        var currentTableArn = await JournalTableArn(s3, bucket);
        Ensure(
            currentTableArn == originalTableArn,
            $"expected the rejected duplicate to leave the original table {Py.Repr(originalTableArn)} in place, got {Py.Repr(currentTableArn)}");

        // Positive control: ordinary data-plane access still works.
        const string key = "still-works.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "still-works" });
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, key));
        var body = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(body.SequenceEqual("still-works"u8.ToArray()), $"expected b'still-works', got {Py.Repr(body)}");
    }

    private static async Task<string?> JournalTableArn(IAmazonS3 s3, string bucket) =>
        (await S3Fidelity.MetadataResult(s3, bucket)).JournalTableConfigurationResult.TableArn;

    private static async Task PollMetadataTableActive(IAmazonS3 s3, string bucket, double timeoutS = 180.0)
    {
        var clock = FidelityTime.StartNew();
        string? lastStatus = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            var journal = (await S3Fidelity.MetadataResult(s3, bucket)).JournalTableConfigurationResult;
            lastStatus = journal.TableStatus;
            if (lastStatus == "ACTIVE")
            {
                return;
            }

            if (lastStatus == "FAILED")
            {
                throw Fail($"metadata configuration failed: {S3Fidelity.Repr(journal.Error)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"metadata configuration never became ACTIVE, last status={lastStatus}");
    }
}
