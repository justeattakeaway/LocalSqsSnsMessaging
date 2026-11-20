// Translated from behavioral/s3/check_op_contract_delete_objects.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-delete-objects</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractDeleteObjects
{
    /// <summary>DeleteObjects batch-deletes versions; a retention-locked version fails per-key with AccessDenied.</summary>
    [Test]
    [DisplayName("s3.op-contract-delete-objects")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateObjectLockBucket(ctx, "sim2real-behav-");

        const string keyLocked = "locked-object.txt";
        const string keyFree = "free-object.txt";
        var vLocked = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = keyLocked, ContentBody = "protected" })).VersionId;
        var vFree = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = keyFree, ContentBody = "unprotected" })).VersionId;

        var retainUntil = FidelityTime.UtcNow.UtcDateTime + TimeSpan.FromDays(1);
        await s3.PutObjectRetentionAsync(new PutObjectRetentionRequest
        {
            BucketName = bucket,
            Key = keyLocked,
            VersionId = vLocked,
            Retention = new ObjectLockRetention { Mode = ObjectLockRetentionMode.Governance, RetainUntilDate = retainUntil },
        });

        // --- success path + partial failure: batch delete of a locked version and a free version ---
        // The .NET SDK raises DeleteObjectsException for a response with Errors; botocore returns it.
        var resp = await DeleteObjects(s3, new DeleteObjectsRequest
        {
            BucketName = bucket,
            Objects = [new KeyVersion { Key = keyLocked, VersionId = vLocked }, new KeyVersion { Key = keyFree, VersionId = vFree }],
            Quiet = false,
        });
        var deletedKeys = (resp.DeletedObjects ?? []).Select(d => (d.Key, d.VersionId)).ToHashSet();
        var errorsByKey = (resp.DeleteErrors ?? []).ToDictionary(e => e.Key, StringComparer.Ordinal);

        Ensure(deletedKeys.Contains((keyFree, vFree)), $"expected {keyFree} deleted, got {Py.Repr(deletedKeys.Select(d => d.Key).ToList())}");
        Ensure(errorsByKey.ContainsKey(keyLocked), $"expected {keyLocked} to fail, got {Py.Repr(errorsByKey.Keys.ToList())}");
        Ensure(errorsByKey[keyLocked].Code == "AccessDenied", $"expected AccessDenied for locked object, got {Py.Repr(errorsByKey[keyLocked].Code)}");
        Ensure(
            errorsByKey[keyLocked].Message == "Access Denied because object protected by object lock.",
            $"unexpected message {Py.Repr(errorsByKey[keyLocked].Message)}");
        Ensure(!deletedKeys.Any(d => d.Key == keyLocked), "locked key must not appear among deleted keys");

        // confirm the locked version genuinely still exists (delete was actually blocked, not just misreported)
        var head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = keyLocked, VersionId = vLocked });
        Ensure(head.VersionId == vLocked, $"expected {keyLocked} version {vLocked} to survive, got {head.VersionId}");

        // --- restore leg: the same batch delete now succeeds once the block is lifted ---
        var resp2 = await DeleteObjects(s3, new DeleteObjectsRequest
        {
            BucketName = bucket,
            Objects = [new KeyVersion { Key = keyLocked, VersionId = vLocked }],
            Quiet = false,
            BypassGovernanceRetention = true,
        });
        var bypassedKeys = (resp2.DeletedObjects ?? []).Select(d => (d.Key, d.VersionId)).ToHashSet();
        Ensure(bypassedKeys.Contains((keyLocked, vLocked)), $"expected bypass delete of {keyLocked} to succeed, got {Py.Repr(resp2.DeleteErrors?.Select(e => e.Code).ToList())}");

        // A HEAD response carries no body, so there is no AWS message to compare.
        var (code, _) = await S3Fidelity.ClientError(
            () => s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = keyLocked, VersionId = vLocked }),
            "expected a ClientError, but the call SUCCEEDED");
        Ensure(code == "404", $"expected the bypassed version to be genuinely gone (404), got {Py.Repr(code)}");
    }

    /// <summary>DeleteObjects as botocore answers it: a response listing Deleted and Errors.</summary>
    private static async Task<DeleteObjectsResponse> DeleteObjects(IAmazonS3 s3, DeleteObjectsRequest request)
    {
        try
        {
            return await s3.DeleteObjectsAsync(request);
        }
        catch (DeleteObjectsException ex)
        {
            return ex.Response;
        }
    }
}
