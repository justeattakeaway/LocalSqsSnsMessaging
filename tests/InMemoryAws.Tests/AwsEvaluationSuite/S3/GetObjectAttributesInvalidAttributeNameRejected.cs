// Translated from behavioral/s3/check_get_object_attributes_invalid_attribute_name_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.get-object-attributes-invalid-attribute-name-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetObjectAttributesInvalidAttributeNameRejected
{
    /// <summary>
    /// GetObjectAttributes with an unrecognized attribute name is rejected with InvalidArgument rather
    /// than ignoring it; the same call with only valid names succeeds and returns the ETag.
    /// </summary>
    [Test]
    [DisplayName("s3.get-object-attributes-invalid-attribute-name-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-s3attrs-");
        const string key = "obj.bin";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "payload-bytes" });
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, key));

        var err = await S3Fidelity.ExpectError(() => s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = bucket,
            Key = key,
            ObjectAttributes = [ObjectAttributes.ETag, new ObjectAttributes("NotARealAttribute")],
        }));
        Ensure(err.Code == "InvalidArgument", $"expected InvalidArgument, got {err}");
        Ensure(err.Message == "Invalid attribute name specified.", err.ToString());

        // Positive control: only valid attribute names on the same object.
        var ok = await s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = bucket,
            Key = key,
            ObjectAttributes = [ObjectAttributes.ETag],
        });
        Ensure(!string.IsNullOrEmpty(ok.ETag), $"positive control did not return an ETag: {Py.Repr(ok.ETag)}");
    }
}
