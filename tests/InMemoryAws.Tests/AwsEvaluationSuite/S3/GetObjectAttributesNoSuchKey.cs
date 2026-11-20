// Translated from behavioral/s3/check_get_object_attributes_no_such_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.get-object-attributes-no-such-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetObjectAttributesNoSuchKey
{
    /// <summary>
    /// GetObjectAttributes on a missing object raises NoSuchKey; a real object in the
    /// same bucket still resolves correctly, before and after the forbidden read.
    /// </summary>
    [Test]
    [DisplayName("s3.get-object-attributes-no-such-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        // Positive control: prove the operation works normally before asserting the negative.
        const string key = "exists.txt";
        var body = "attributes-positive-control"u8.ToArray();
        var put = await S3Fidelity.PutWithClass(s3, bucket, key, body);
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, key));
        var attrs = await Attributes(s3, bucket, key, "ETag", "StorageClass", "ObjectSize");
        Ensure(attrs.ObjectSize == body.Length, $"expected size {body.Length}, got {attrs.ObjectSize}");
        Ensure(attrs.ETag.Trim('"') == put.ETag.Trim('"'), $"ETag mismatch: {attrs.ETag} vs {put.ETag}");
        var err = await S3Fidelity.Refusal(() => Attributes(s3, bucket, "never-existed.txt", "ETag", "StorageClass", "ObjectSize"));
        Ensure(err.Code == "NoSuchKey", $"expected NoSuchKey, got {S3Fidelity.Repr(err)}");
        Ensure(err.Message == "The specified key does not exist.", S3Fidelity.Repr(err));

        // The forbidden read did not disturb the real object's attributes.
        var attrsAgain = await Attributes(s3, bucket, key, "ETag");
        Ensure(attrsAgain.ETag.Trim('"') == put.ETag.Trim('"'), "real object's ETag changed after a read of a missing key");
    }

    private static Task<GetObjectAttributesResponse> Attributes(AmazonS3Client s3, string bucket, string key, params string[] attributes) =>
        s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = bucket,
            Key = key,
            ObjectAttributes = [.. attributes.Select(a => new ObjectAttributes(a))],
        });
}
