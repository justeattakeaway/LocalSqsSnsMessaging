// Translated from behavioral/s3/check_delete_objects_partial_failure.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.delete-objects-partial-failure</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteObjectsPartialFailure
{
    /// <summary>DeleteObjects processes a mixed batch per-key: valid deletes succeed while a bogus VersionId reports a per-key error.</summary>
    [Test]
    [DisplayName("s3.delete-objects-partial-failure")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest { BucketName = bucket, VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled } });
        const string goodKey = "batch-good.txt";
        var realVersion = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = goodKey, ContentBody = "payload" })).VersionId;
        const string badKey = "batch-bad.txt";
        var badRealVersion = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = badKey, ContentBody = "payload2" })).VersionId;
        var bogusVersion = realVersion[..^1] + (realVersion[^1] != 'A' ? "A" : "B");

        // The .NET SDK raises DeleteObjectsException for a response with Errors; botocore returns it.
        DeleteObjectsResponse resp;
        try
        {
            resp = await s3.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = bucket,
                Objects = [new KeyVersion { Key = goodKey, VersionId = realVersion }, new KeyVersion { Key = badKey, VersionId = bogusVersion }],
                Quiet = false,
            });
        }
        catch (DeleteObjectsException ex)
        {
            resp = ex.Response;
        }

        var deletedKeys = (resp.DeletedObjects ?? []).Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        var errors = (resp.DeleteErrors ?? []).ToDictionary(e => e.Key, StringComparer.Ordinal);
        Ensure(deletedKeys.Contains(goodKey), $"expected {Py.Repr(goodKey)} in Deleted, got {Py.Repr(deletedKeys.ToList())}");
        var badErr = errors.GetValueOrDefault(badKey);
        Ensure(badErr?.Code == "NoSuchVersion", $"expected NoSuchVersion error for {Py.Repr(badKey)}, got {Py.Repr(errors.Values.Select(e => (e.Key, e.Code, e.Message)).ToList())}");
        Ensure(badErr.Message == "The specified version does not exist.", badErr.Message ?? "None");

        // independent read at the destination: the deleted version is actually gone, not just reported gone
        // HeadObject answers with no body, so there is no service message to grade beyond the status code
        var (goneCode, _) = await S3Fidelity.ClientError(
            () => s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = goodKey, VersionId = realVersion }),
            $"HeadObject of deleted version {Py.Repr(realVersion)} succeeded; expected a refusal");
        Ensure(goneCode == "404", $"expected {Py.Repr(goodKey)} version {Py.Repr(realVersion)} gone (404), got {Py.Repr(goneCode)}");

        // independent read: the per-key error on bad_key actually withheld the delete of its real version
        var survivor = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = badKey, VersionId = badRealVersion });
        Ensure(
            survivor.VersionId == badRealVersion,
            $"expected {Py.Repr(badKey)} version {Py.Repr(badRealVersion)} untouched by the failed delete, "
            + $"got VersionId={Py.Repr(survivor.VersionId)}");
    }
}
