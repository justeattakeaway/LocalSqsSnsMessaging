// Translated from behavioral/s3/check_encryption_enforcement_reject_unencrypted.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.encryption-enforcement-reject-unencrypted</summary>
[Category("AwsEvaluationSuite")]
public sealed class EncryptionEnforcementRejectUnencrypted
{
    /// <summary>
    /// When the bucket mandates SSE-KMS (default encryption + a policy denying writes that omit the header), a PutObject omitting it MUST be rejected.
    /// The write must not be silently accepted (and definitely not silently encrypted anyway).
    /// </summary>
    [Test]
    [DisplayName("s3.encryption-enforcement-reject-unencrypted")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        // Positive control, before any enforcement exists: a plain PutObject to a fresh bucket
        // succeeds. Without this, an AccessDenied later cannot be distinguished from a bucket that
        // rejects every write for an unrelated reason.
        const string controlKey = "control-before-policy.bin";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = controlKey, ContentBody = "baseline-write-ok" });
        await s3.GetObjectMetadataAsync(bucket, controlKey);

        await s3.PutBucketEncryptionAsync(new PutBucketEncryptionRequest
        {
            BucketName = bucket,
            ServerSideEncryptionConfiguration = new ServerSideEncryptionConfiguration
            {
                ServerSideEncryptionRules =
                [
                    new ServerSideEncryptionRule
                    {
                        ServerSideEncryptionByDefault = new ServerSideEncryptionByDefault { ServerSideEncryptionAlgorithm = ServerSideEncryptionMethod.AWSKMS },
                        BucketKeyEnabled = true,
                    },
                ],
            },
        });
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "DenyUnlessKmsEncrypted",
                ["Effect"] = "Deny",
                ["Principal"] = "*",
                ["Action"] = "s3:PutObject",
                ["Resource"] = $"arn:aws:s3:::{bucket}/*",
                ["Condition"] = new JsonObject { ["StringNotEquals"] = new JsonObject { ["s3:x-amz-server-side-encryption"] = "aws:kms" } },
            }),
        };
        await s3.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = bucket, Policy = policy.ToJsonString() });
        try
        {
            const string key = "must-be-encrypted.bin";
            var callerArn = ctx.CallerArn;
            var denied = await S3Fidelity.Error(
                () => s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "plaintext-attempt" }),
                "expected this call to fail, but it SUCCEEDED");
            var want = (
                "AccessDenied",
                $"User: {callerArn} is not authorized to perform: s3:PutObject on resource: " +
                $"\"arn:aws:s3:::{bucket}/{key}\" with an explicit deny in a resource-based policy");
            Ensure((denied.Code, denied.Message) == want, $"expected the policy's AccessDenied for PutObject missing SSE-KMS header, got {S3Fidelity.Repr((denied.Code, denied.Message))}");

            // object must not exist -- the rejected write had no side effect
            // HeadObject answers with no body, so its 404 status is the whole refusal to grade
            var headError = (await S3Fidelity.HeadError(s3, bucket, key, "expected this call to fail, but it SUCCEEDED")).Code;
            Ensure(headError == "404", $"expected the rejected write to leave no object (404 on HeadObject), got {headError}");

            // a correctly-encrypted PutObject succeeds
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ContentBody = "ciphertext-ok",
                ServerSideEncryptionMethod = ServerSideEncryptionMethod.AWSKMS,
            });
            var head = await s3.GetObjectMetadataAsync(bucket, key);
            Ensure(
                head.ServerSideEncryptionMethod == ServerSideEncryptionMethod.AWSKMS,
                $"expected the compliant PutObject to be stored as aws:kms, got {Py.Repr(head.ServerSideEncryptionMethod?.Value)}");
        }
        finally
        {
            await s3.DeleteBucketPolicyAsync(new DeleteBucketPolicyRequest { BucketName = bucket });
        }
    }
}
