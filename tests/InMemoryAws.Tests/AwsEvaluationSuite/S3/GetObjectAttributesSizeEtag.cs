// Translated from behavioral/s3/check_get_object_attributes_size_etag.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.get-object-attributes-size-etag</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetObjectAttributesSizeEtag
{
    /// <summary>
    /// GetObjectAttributes reports ObjectSize and ETag consistent with the stored object.
    /// </summary>
    [Test]
    [DisplayName("s3.get-object-attributes-size-etag")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "attrs.bin";
        var body = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("attribute-payload", 64)));
        await S3Fidelity.PutWithClass(s3, bucket, key, body);
        var attrs = await Attributes(s3, bucket, key, "ETag", "ObjectSize", "StorageClass");
        Ensure(attrs.ObjectSize == body.Length, $"expected ObjectSize {body.Length}, got {attrs.ObjectSize}");
        Ensure(
            attrs.ETag.Trim('"') == Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(body)),
            $"GetObjectAttributes ETag disagrees with the object MD5: {Py.Repr(attrs.ETag)}");
        Ensure(
            (attrs.StorageClass?.Value ?? "STANDARD") == "STANDARD",
            $"expected StorageClass STANDARD (or omitted), got {Py.Repr(attrs.StorageClass?.Value)}");
    }

    private static Task<GetObjectAttributesResponse> Attributes(AmazonS3Client s3, string bucket, string key, params string[] attributes) =>
        s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = bucket,
            Key = key,
            ObjectAttributes = [.. attributes.Select(a => new ObjectAttributes(a))],
        });
}
