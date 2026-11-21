// Translated from behavioral/s3/check_bucket_metadata_table_v1_surface_closed.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-metadata-table-v1-surface-closed</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketMetadataTableV1SurfaceClosed
{
    /// <summary>
    /// The V1 CreateBucketMetadataTableConfiguration/Get/Delete trio is refused on a bucket that
    /// never had a V1 configuration, while the V2 GetBucketMetadataConfiguration on the same, still
    /// unconfigured bucket answers the ordinary MetadataConfigurationNotFound.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-metadata-table-v1-surface-closed")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-metatblv1-");

        // Control: the V2 surface is live and answers its documented not-configured error.
        var (v2Code, _) = await Error(() => s3.GetBucketMetadataConfigurationAsync(new GetBucketMetadataConfigurationRequest { BucketName = bucket }));
        Ensure(
            v2Code == "MetadataConfigurationNotFound",
            $"GetBucketMetadataConfiguration on an unconfigured bucket returned {Py.Repr(v2Code)}, expected MetadataConfigurationNotFound");

        static string ClosureMessage(string v1Op, string v2Op) =>
            $"The V1 {v1Op} API operation isn't available for this account. Use the corresponding V2 {v2Op} API operation instead.";

        var (getCode, getMessage) = await Error(() => s3.GetBucketMetadataTableConfigurationAsync(new GetBucketMetadataTableConfigurationRequest { BucketName = bucket }));
        Ensure(
            getCode == "V1APIsNotAllowed",
            $"GetBucketMetadataTableConfiguration on an account where the V1 surface is closed answered {Py.Repr(getCode)}, expected V1APIsNotAllowed");
        Ensure(
            getCode != v2Code,
            $"GetBucketMetadataTableConfiguration answered the same code as the V2 Get ({Py.Repr(getCode)}); the V1 surface should be closed, not merely unconfigured");
        Ensure(
            getMessage == ClosureMessage("GetBucketMetadataTableConfiguration", "GetBucketMetadataConfiguration"),
            $"unexpected message for the closed V1 Get: {Py.Repr(getMessage)}");

        var (deleteCode, deleteMessage) = await Error(() => s3.DeleteBucketMetadataTableConfigurationAsync(new DeleteBucketMetadataTableConfigurationRequest { BucketName = bucket }));
        Ensure(
            deleteCode == getCode,
            $"DeleteBucketMetadataTableConfiguration answered {Py.Repr(deleteCode)}, expected the same closure code as Get ({Py.Repr(getCode)})");
        Ensure(
            deleteMessage == ClosureMessage("DeleteBucketMetadataTableConfiguration", "DeleteBucketMetadataConfiguration"),
            $"unexpected message for the closed V1 Delete: {Py.Repr(deleteMessage)}");

        var tableArn = $"arn:aws:s3tables:{ctx.Region}:{ctx.Account}:bucket/sim2real-metatblv1-dest";
        var (createCode, createMessage) = await Error(() => s3.CreateBucketMetadataTableConfigurationAsync(new CreateBucketMetadataTableConfigurationRequest
        {
            BucketName = bucket,
            MetadataTableConfiguration = new MetadataTableConfiguration
            {
                S3TablesDestination = new S3TablesDestination { TableBucketArn = tableArn, TableName = "sim2real_metadata" },
            },
        }));
        Ensure(
            createCode == getCode,
            $"CreateBucketMetadataTableConfiguration answered {Py.Repr(createCode)}, expected the same closure code as Get and Delete ({Py.Repr(getCode)})");
        Ensure(
            createMessage == ClosureMessage("CreateBucketMetadataTableConfiguration", "CreateBucketMetadataConfiguration"),
            $"unexpected message for the closed V1 Create: {Py.Repr(createMessage)}");

        // Restore/positive control, repeated: the V2 surface is still unaffected by the three V1 calls.
        var (afterCode, _) = await Error(() => s3.GetBucketMetadataConfigurationAsync(new GetBucketMetadataConfigurationRequest { BucketName = bucket }));
        Ensure(
            afterCode == "MetadataConfigurationNotFound",
            $"GetBucketMetadataConfiguration after the V1 refusals returned {Py.Repr(afterCode)}, expected MetadataConfigurationNotFound, unchanged");
    }

    /// <summary><c>_error(fn)</c>: (Code, Message) of a refused call; an unexpected success fails the check.</summary>
    private static Task<(string? Code, string? Message)> Error(Func<Task> call) =>
        S3Fidelity.ErrorPair(call, "expected the call to be refused, but it succeeded");
}
