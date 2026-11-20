// Translated from behavioral/s3/check_ssec_head_object_requires_the_matching_customer_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.ssec-head-object-requires-the-matching-customer-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class SsecHeadObjectRequiresTheMatchingCustomerKey
{
    /// <summary>
    /// HeadObject on an SSE-C object needs the object's customer key: absent is refused 400,
    /// wrong is refused 403, and the right key echoes SSECustomerAlgorithm and its key MD5.
    /// </summary>
    [Test]
    [DisplayName("s3.ssec-head-object-requires-the-matching-customer-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await AllowSsec(s3, bucket);
        var keyBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var otherKeyBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var md5 = Convert.ToBase64String(System.Security.Cryptography.MD5.HashData(keyBytes));
        var key = Convert.ToBase64String(keyBytes);
        const string obj = "ssec-head.bin";
        var put = await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = obj,
            ContentBody = "ssec-head-object-body",
            ServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256,
            ServerSideEncryptionCustomerProvidedKey = key,
        });
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, obj));
        Ensure(
            put.ServerSideEncryptionCustomerMethod?.Value == "AES256",
            $"PutObject echoed SSECustomerAlgorithm {Py.Repr(put.ServerSideEncryptionCustomerMethod?.Value)}, expected 'AES256'");
        Ensure(
            put.ServerSideEncryptionCustomerProvidedKeyMD5 == md5,
            $"PutObject echoed key MD5 {Py.Repr(put.ServerSideEncryptionCustomerProvidedKeyMD5)}, expected {Py.Repr(md5)}");

        // --- No key: refused 400, with the generic HTTP reason (HEAD carries no error body).
        var (code, status, msg) = await Error(() => s3.GetObjectMetadataAsync(bucket, obj));
        Ensure(
            (code, status, msg) == ("400", 400, "Bad Request"),
            $"HeadObject without the key returned {Py.Repr(code)}/{status} ({Py.Repr(msg)}); expected '400'/400 ('Bad Request')");

        // --- Wrong key: refused 403, distinctly from the missing-key case.
        (code, status, msg) = await Error(() => s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = bucket,
            Key = obj,
            ServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256,
            ServerSideEncryptionCustomerProvidedKey = Convert.ToBase64String(otherKeyBytes),
        }));
        Ensure(
            (code, status, msg) == ("403", 403, "Forbidden"),
            $"HeadObject with the wrong key returned {Py.Repr(code)}/{status} ({Py.Repr(msg)}); expected '403'/403 ('Forbidden')");

        // --- Positive control: the right key succeeds and echoes the algorithm and key MD5.
        var head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = bucket,
            Key = obj,
            ServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256,
            ServerSideEncryptionCustomerProvidedKey = key,
        });
        Ensure(
            head.ServerSideEncryptionCustomerMethod?.Value == "AES256",
            $"HeadObject with the right key reported SSECustomerAlgorithm {Py.Repr(head.ServerSideEncryptionCustomerMethod?.Value)}, expected 'AES256'");
        Ensure(
            head.ServerSideEncryptionCustomerProvidedKeyMD5 == md5,
            $"HeadObject with the right key reported key MD5 {Py.Repr(head.ServerSideEncryptionCustomerProvidedKeyMD5)}, expected {Py.Repr(md5)}");
    }

    /// <summary>_error(fn): botocore's (Code, HTTP status, Message) of the refusal; a success fails the check.</summary>
    private static async Task<(string? Code, int Status, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn) ?? throw Fail("expected a ClientError, but the call SUCCEEDED");
        var (code, message) = S3Fidelity.Botocore(error);
        return (code, (int)error.StatusCode, message);
    }

    /// <summary>_allow_ssec(s3, bucket): opts THIS bucket out of the SSE-C upload block and asserts the opt-out is what is stored.</summary>
    private static async Task AllowSsec(AmazonS3Client s3, string bucket)
    {
        await s3.PutBucketEncryptionAsync(new PutBucketEncryptionRequest
        {
            BucketName = bucket,
            ServerSideEncryptionConfiguration = new ServerSideEncryptionConfiguration
            {
                ServerSideEncryptionRules =
                [
                    new ServerSideEncryptionRule
                    {
                        ServerSideEncryptionByDefault = new ServerSideEncryptionByDefault { ServerSideEncryptionAlgorithm = ServerSideEncryptionMethod.AES256 },
                        BlockedEncryptionTypes = new BlockedEncryptionTypes { EncryptionType = ["NONE"] },
                    },
                ],
            },
        });
        var rules = (await s3.GetBucketEncryptionAsync(new GetBucketEncryptionRequest { BucketName = bucket })).ServerSideEncryptionConfiguration.ServerSideEncryptionRules;
        var blocked = rules[0].BlockedEncryptionTypes?.EncryptionType?.Select(t => (string)t).ToList();
        Ensure(blocked is ["NONE"], $"bucket still blocks {Py.Repr(blocked)} after the SSE-C opt-out");
    }
}
