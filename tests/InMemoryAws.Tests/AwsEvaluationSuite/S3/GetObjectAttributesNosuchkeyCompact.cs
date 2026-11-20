// Translated from behavioral/s3/check_get_object_attributes_nosuchkey.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>
/// s3.get-object-attributes-nosuchkey (named apart from <see cref="GetObjectAttributesNoSuchKey"/>, its near
/// twin <c>check_get_object_attributes_no_such_key.py</c>: the build refuses two file names that differ only in case).
/// </summary>
[Category("AwsEvaluationSuite")]
public sealed class GetObjectAttributesNosuchkeyCompact
{
    /// <summary>
    /// GetObjectAttributes on a missing key returns NoSuchKey rather than an
    /// empty/partial attribute set.
    /// </summary>
    [Test]
    [DisplayName("s3.get-object-attributes-nosuchkey")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");
        var err = await S3Fidelity.Refusal(() => Attributes(s3, bucket, "does-not-exist.bin", "ObjectSize", "ETag", "StorageClass"));
        Ensure(err.Code == "NoSuchKey", $"expected NoSuchKey, got {S3Fidelity.Repr(err)}");
        Ensure(err.Message == "The specified key does not exist.", S3Fidelity.Repr(err));

        // Sanity: a real object returns real attributes.
        const string key = "real-object.bin";
        var body = "attribute-check-payload"u8.ToArray();
        await S3Fidelity.PutWithClass(s3, bucket, key, body);
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, key));
        var attrs = await Attributes(s3, bucket, key, "ObjectSize", "ETag", "StorageClass");
        Ensure(attrs.ObjectSize == body.Length, "assert attrs[\"ObjectSize\"] == len(body)");
        Ensure(attrs.StorageClass?.Value == "STANDARD", "assert attrs[\"StorageClass\"] == \"STANDARD\"");
    }

    private static Task<GetObjectAttributesResponse> Attributes(AmazonS3Client s3, string bucket, string key, params string[] attributes) =>
        s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = bucket,
            Key = key,
            ObjectAttributes = [.. attributes.Select(a => new ObjectAttributes(a))],
        });
}
