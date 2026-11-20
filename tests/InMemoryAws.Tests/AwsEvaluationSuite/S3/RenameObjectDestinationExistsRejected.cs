// Translated from behavioral/s3/check_rename_object_destination_exists_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.rename-object-destination-exists-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class RenameObjectDestinationExistsRejected
{
    /// <summary>
    /// RenameObject with If-None-Match: * succeeds onto an absent destination, fails with
    /// PreconditionFailed and its exact message when the destination exists (both objects untouched),
    /// and succeeds once the destination is removed.
    /// </summary>
    [Test]
    [DisplayName("s3.rename-object-destination-exists-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var (bucket, _) = await S3Fidelity.CreateDirectoryBucket(ctx, s3, FidelityContext.UniqueName("sim2real-db-"), S3Fidelity.AvailabilityZoneIds(ctx));
        const string sourceKey = "source-for-clash.txt";
        const string destKey = "existing-destination.txt";
        var sourceBody = "source content"u8.ToArray();
        var destBody = "pre-existing destination content"u8.ToArray();

        // Positive control: the same guarded rename succeeds when the destination does not exist yet,
        // so a later rejection is attributable to the clash and not to a broken RenameObject wiring.
        const string controlSourceKey = "control-source-for-clash.txt";
        var controlBody = "control source content"u8.ToArray();
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = controlSourceKey, InputStream = new MemoryStream(controlBody) });
        await Rename(s3, bucket, destKey, controlSourceKey);
        var headControl = await s3.GetObjectMetadataAsync(bucket, destKey);
        Ensure(
            headControl.ContentLength == controlBody.Length,
            "positive control: expected the guarded rename onto a fresh destination to succeed and " +
            $"land {controlBody.Length} bytes, got {headControl.ContentLength}");

        // Break exactly one thing: put_source/put_dest give dest_key real, pre-existing content, which
        // is the clash the guard exists to detect.
        var putSource = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = sourceKey, InputStream = new MemoryStream(sourceBody) });
        var putDest = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = destKey, InputStream = new MemoryStream(destBody) });
        var code = await ErrorCode(() => Rename(s3, bucket, destKey, sourceKey));
        Ensure(
            code == ("PreconditionFailed", "At least one of the pre-conditions you specified did not hold"),
            $"expected PreconditionFailed, got {S3Fidelity.Repr(code)}");

        // both objects must be untouched by the rejected rename
        var headSource = await s3.GetObjectMetadataAsync(bucket, sourceKey);
        Ensure(
            headSource.ETag == putSource.ETag,
            $"source ETag changed across a rejected rename: expected {Py.Repr(putSource.ETag)}, got {Py.Repr(headSource.ETag)}");
        Ensure(
            headSource.ContentLength == sourceBody.Length,
            $"source length changed across a rejected rename: expected {sourceBody.Length}, got {headSource.ContentLength}");
        var headDest = await s3.GetObjectMetadataAsync(bucket, destKey);
        Ensure(
            headDest.ETag == putDest.ETag,
            $"destination ETag changed across a rejected rename: expected {Py.Repr(putDest.ETag)}, got {Py.Repr(headDest.ETag)}");
        Ensure(
            headDest.ContentLength == destBody.Length,
            $"destination length changed across a rejected rename: expected {destBody.Length}, got {headDest.ContentLength}");

        // Restore: remove the clashing destination, then replay the IDENTICAL call. Real AWS's own
        // guard, not this check, decides whether the rename may now proceed.
        await s3.DeleteObjectAsync(bucket, destKey);
        await Rename(s3, bucket, destKey, sourceKey);
        var headAfterRestore = await s3.GetObjectMetadataAsync(bucket, destKey);
        Ensure(
            headAfterRestore.ContentLength == sourceBody.Length,
            "after removing the clashing destination, the identical rename should have landed " +
            $"{sourceBody.Length} source bytes, got {headAfterRestore.ContentLength}");
        var sourceGoneCode = await ErrorCode(() => s3.GetObjectMetadataAsync(bucket, sourceKey));
        Ensure(
            sourceGoneCode == ("404", "Not Found"),
            $"the successful rename should have consumed the source key, but head_object on {Py.Repr(sourceKey)} returned {S3Fidelity.Repr(sourceGoneCode)}");
    }

    private static Task<RenameObjectResponse> Rename(IAmazonS3 s3, string bucket, string key, string source) =>
        s3.RenameObjectAsync(new RenameObjectRequest { BucketName = bucket, Key = key, RenameSource = source, DestinationIfNoneMatch = "*" });

    /// <summary>The (code, message) botocore reports for the refusal; a bare HEAD error's code is its status.</summary>
    private static async Task<(string? Code, string? Message)> ErrorCode(Func<Task> call) =>
        S3Fidelity.Botocore(await S3Fidelity.Error(call, "expected a rejection, but the call SUCCEEDED"));
}
