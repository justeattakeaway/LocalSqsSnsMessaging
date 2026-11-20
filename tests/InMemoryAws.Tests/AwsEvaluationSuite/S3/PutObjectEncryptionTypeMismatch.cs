// Translated from behavioral/s3/check_put_object_encryption_type_mismatch.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-object-encryption-type-mismatch</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutObjectEncryptionTypeMismatch
{
    private const string Check = "put-object-encryption-type-mismatch";

    /// <summary>
    /// On a bucket whose default encryption blocks SSE-C, CreateMultipartUpload with SSE-C headers is
    /// refused with AccessDenied and the exact SSE-C-blocked message and leaves no pending upload, while a
    /// plain multipart upload on the same bucket works.
    /// </summary>
    [Test]
    [DisplayName("s3.put-object-encryption-type-mismatch")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = bucket, TagSet = [new Tag { Key = "sim2real", Value = Check }] });
        const string key = "ssec-multipart-object.bin";
        var sseKey = RandomNumberGenerator.GetBytes(32);

        var control = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key });
        await s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = bucket, Key = key, UploadId = control.UploadId });

        var error = await S3Fidelity.Error(
            () => s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
            {
                BucketName = bucket,
                Key = key,
                ServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256,
                ServerSideEncryptionCustomerProvidedKey = Convert.ToBase64String(sseKey),
            }),
            "real AWS rejects this call, but it SUCCEEDED");
        var callerArn = ctx.CallerArn;
        var want = (
            "AccessDenied",
            $"User: {callerArn} is not authorized to perform: s3:PutObject on resource: " +
            $"\"arn:aws:s3:::{bucket}/{key}\" because this bucket has blocked upload requests that " +
            "specify Server Side Encryption with Customer provided keys (SSE-C). Please specify a " +
            "different server-side encryption type.");
        Ensure(
            (error.Code, error.Message) == want,
            "AccessDenied on CreateMultipartUpload must name the SSE-C block specifically, so this " +
            $"check cannot be satisfied by a target that denies every request; got {Py.Repr(error.Code)} ({Py.Repr(error.Message)})");

        var pending = ((await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket })).MultipartUploads ?? [])
            .Where(u => u.Key == key)
            .Select(u => u.UploadId)
            .ToList();
        Ensure(
            pending.Count == 0,
            $"the SSE-C CreateMultipartUpload was denied but left upload id(s) [{string.Join(", ", pending.Select(Py.Repr))}] registered " +
            $"on {Py.Repr(key)}");
    }
}
