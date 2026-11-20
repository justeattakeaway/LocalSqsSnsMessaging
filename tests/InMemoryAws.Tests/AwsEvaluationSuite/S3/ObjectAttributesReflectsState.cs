// Translated from behavioral/s3/check_object_attributes_reflects_state.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-attributes-reflects-state</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectAttributesReflectsState
{
    /// <summary>
    /// GetObjectAttributes tracks ETag/size/class through create, delete-then-unusable, and recreate.
    /// </summary>
    [Test]
    [DisplayName("s3.object-attributes-reflects-state")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-attrs-");
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = bucket, TagSet = [new Tag { Key = "simparity-check", Value = "object-attributes-reflects-state" }] });
        const string key = "attributes-object.bin";
        var body = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("attribute-tracking-payload", 10)));
        var put = await S3Fidelity.PutWithClass(s3, bucket, key, body);
        var putEtag = put.ETag.Trim('"');
        var attrs = await Attributes(s3, bucket, key, "ETag", "ObjectSize", "StorageClass");
        Ensure(attrs.ETag.Trim('"') == putEtag, $"ETag mismatch: {attrs.ETag} vs {putEtag}");
        Ensure(attrs.ObjectSize == body.Length, $"ObjectSize mismatch: {attrs.ObjectSize} vs {body.Length}");

        // STANDARD is the default storage class and is often omitted rather than echoed back.
        Ensure(attrs.StorageClass?.Value is null or "STANDARD", $"unexpected StorageClass: {attrs.StorageClass?.Value}");
        await s3.DeleteObjectAsync(bucket, key);
        var (code, message) = await S3Fidelity.Refusal(() => Attributes(s3, bucket, key, "ETag", "ObjectSize"));
        Ensure(code == "NoSuchKey", $"expected NoSuchKey after delete, got {code}");
        Ensure(message == "The specified key does not exist.", $"unexpected NoSuchKey message: {Py.Repr(message)}");

        // The deleted id isn't just unread — it's unusable: a mutation against it fails the same way.
        var (tagCode, tagMessage) = await S3Fidelity.Refusal(() => s3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = bucket,
            Key = key,
            Tagging = new Tagging { TagSet = [new Tag { Key = "k", Value = "v" }] },
        }));
        Ensure(tagCode == "NoSuchKey", $"expected a mutation on the deleted key to fail too, got {tagCode}");
        Ensure(tagMessage == "The specified key does not exist.", $"unexpected NoSuchKey message: {Py.Repr(tagMessage)}");

        // The identifier is reusable, and attributes reflect the NEW state, not the deleted one.
        var newBody = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("post-delete-replacement-payload", 5)));
        var put2 = await S3Fidelity.PutWithClass(s3, bucket, key, newBody);
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, key));
        var put2Etag = put2.ETag.Trim('"');
        var attrs2 = await Attributes(s3, bucket, key, "ETag", "ObjectSize");
        Ensure(attrs2.ETag.Trim('"') == put2Etag, $"ETag mismatch after recreate: {attrs2.ETag} vs {put2Etag}");
        Ensure(attrs2.ObjectSize == newBody.Length, $"ObjectSize mismatch after recreate: {attrs2.ObjectSize} vs {newBody.Length}");
        Ensure(attrs2.ETag.Trim('"') != putEtag, "recreated object still reports the deleted object's ETag");
    }

    private static Task<GetObjectAttributesResponse> Attributes(AmazonS3Client s3, string bucket, string key, params string[] attributes) =>
        s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = bucket,
            Key = key,
            ObjectAttributes = [.. attributes.Select(a => new ObjectAttributes(a))],
        });
}
