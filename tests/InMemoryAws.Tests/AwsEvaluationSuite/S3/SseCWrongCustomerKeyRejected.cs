// Translated from behavioral/s3/check_sse_c_wrong_customer_key_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.sse-c-wrong-customer-key-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SseCWrongCustomerKeyRejected
{
    /// <summary>
    /// On a new bucket, PutObject and CreateMultipartUpload with SSE-C headers fail with AccessDenied and
    /// the exact SSE-C-blocked message naming the caller and leave no object, while a plain PutObject and
    /// GetObject on the same bucket work.
    /// </summary>
    [Test]
    [DisplayName("s3.sse-c-wrong-customer-key-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var caller = ctx.CallerArn;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "sse-c-object.bin";
        const string body = "customer-encrypted-payload";
        var good = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = body });
        Ensure(
            Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket, key)) == body,
            "a plain PutObject/GetObject round-trip must succeed before asserting the SSE-C block");
        await s3.DeleteObjectAsync(bucket, key);

        var exc = await FidelityContext.CaptureError(() => s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ContentBody = body,
            ServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256,
            ServerSideEncryptionCustomerProvidedKey = good,
        }));
        Ensure(
            exc is not null,
            "PutObject with SSE-C headers succeeded; this account/region is expected to block SSE-C " +
            "uploads account-wide");
        Ensure(exc.Code == "AccessDenied", $"expected AccessDenied for a blocked SSE-C PutObject, got {Py.Repr(exc.Code)}");
        Ensure(exc.StatusCode == HttpStatusCode.Forbidden, $"expected HTTP 403 for a blocked SSE-C PutObject, got {(int)exc.StatusCode}");
        var blocked =
            $"User: {caller} is not authorized to perform: s3:PutObject on resource: \"arn:aws:s3:::{bucket}/{key}\" " +
            "because this bucket has blocked upload requests that specify Server Side Encryption with " +
            "Customer provided keys (SSE-C). Please specify a different server-side encryption type.";
        Ensure(exc.Message == blocked, $"expected {Py.Repr(blocked)}, got {Py.Repr(exc.Message)}");

        exc = await FidelityContext.CaptureError(() => s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            ServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256,
            ServerSideEncryptionCustomerProvidedKey = good,
        }));
        Ensure(exc is not null, "CreateMultipartUpload with SSE-C headers succeeded; expected the same account-wide block");
        Ensure(exc.Code == "AccessDenied", $"expected AccessDenied for a blocked SSE-C CreateMultipartUpload, got {Py.Repr(exc.Code)}");
        Ensure(exc.Message == blocked, $"expected {Py.Repr(blocked)}, got {Py.Repr(exc.Message)}");

        var listed = ((await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket })).S3Objects ?? []).Select(o => o.Key).ToList();
        Ensure(listed.Count == 0, $"expected no objects after both SSE-C attempts were refused, got [{string.Join(", ", listed.Select(Py.Repr))}]");
    }
}
