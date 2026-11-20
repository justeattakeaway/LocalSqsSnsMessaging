// Translated from behavioral/s3/check_put_object_encryption_type_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-object-encryption-type-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutObjectEncryptionTypeMismatchRejected
{
    /// <summary>
    /// Appending to a directory-bucket object with a different SSE type than its write session is
    /// rejected with InvalidRequest (not EncryptionTypeMismatch) and leaves the object unchanged;
    /// appends with the matching default encryption succeed before and after.
    /// </summary>
    [Test]
    [DisplayName("s3.put-object-encryption-type-mismatch-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var (bucket, _) = await S3Fidelity.CreateDirectoryBucket(ctx, s3, FidelityContext.UniqueName("sim2realdb"), S3Fidelity.ExpressZoneIds[ctx.Region]);
        var key = FidelityContext.UniqueName("append-enc") + ".log";
        var initialBody = "seed-data!"u8.ToArray(); // implicitly SSE-S3 encrypted (directory bucket default)
        var created = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(initialBody) });
        Ensure(
            created.ServerSideEncryptionMethod?.Value is null or "AES256",
            $"expected the default directory-bucket encryption (None or AES256), got {Py.Repr(created.ServerSideEncryptionMethod?.Value)}");

        // Positive control: an append that doesn't change the SSE type succeeds.
        var matchingChunk = "-more"u8.ToArray();
        await S3Fidelity.Append(s3, bucket, key, matchingChunk, initialBody.Length);
        var sizeAfterGoodAppend = initialBody.Length + matchingChunk.Length;
        var afterGoodAppend = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(
            afterGoodAppend.ContentLength == sizeAfterGoodAppend,
            $"expected {sizeAfterGoodAppend} bytes after the matching-encryption append, got {afterGoodAppend.ContentLength}");

        // The check creates a KMS key only to name it in the refused request, which never reaches KMS;
        // a well-formed key ARN stands in.
        var keyArn = $"arn:aws:kms:{ctx.Region}:{ctx.Account}:key/{Guid.NewGuid()}";

        // Negative: appending with a different SSE type (SSE-KMS) than the object's current
        // SSE-S3 encryption is rejected — as InvalidRequest, not EncryptionTypeMismatch. Real AWS
        // attributes the rejection to the write SESSION having been established with AES256, and
        // says so in the message; that message is asserted too, to separate this from any other
        // malformed-request InvalidRequest.
        var (code, message) = await S3Fidelity.ErrorPair(() => s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = new MemoryStream("-kms-chunk"u8.ToArray()),
            WriteOffsetBytes = sizeAfterGoodAppend,
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS,
            ServerSideEncryptionKeyManagementServiceKeyId = keyArn,
        }), "expected a rejection, but the call SUCCEEDED");
        Ensure(
            (code, message) == ("InvalidRequest", "Session was created with ServerSideEncryption as AES256 but request header specifies SSE-KMS."),
            "expected the InvalidRequest message to name the session's committed encryption " +
            $"and the mismatched SSE-KMS header, got {Py.Repr(code)} ({Py.Repr(message)})");

        // The rejected append must not have mutated the object.
        var stillUnchanged = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(
            stillUnchanged.ContentLength == sizeAfterGoodAppend,
            $"the rejected mismatched-encryption append changed the object length: expected {sizeAfterGoodAppend}, got {stillUnchanged.ContentLength}");
        Ensure(
            stillUnchanged.ServerSideEncryptionMethod?.Value is null or "AES256",
            $"the rejected mismatched-encryption append changed the object's encryption type to {Py.Repr(stillUnchanged.ServerSideEncryptionMethod?.Value)}");

        // Restore: retry the identical append with the object's actual (matching) encryption
        // type. It must now succeed, proving the earlier rejection was specific to the
        // encryption mismatch and did not wedge the object against further appends.
        var restoredChunk = "-restored"u8.ToArray();
        await S3Fidelity.Append(s3, bucket, key, restoredChunk, sizeAfterGoodAppend);
        var sizeAfterRestore = sizeAfterGoodAppend + restoredChunk.Length;
        var restored = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(
            restored.ContentLength == sizeAfterRestore,
            $"expected {sizeAfterRestore} bytes after the restored matching-encryption append, got {restored.ContentLength}");
    }
}
