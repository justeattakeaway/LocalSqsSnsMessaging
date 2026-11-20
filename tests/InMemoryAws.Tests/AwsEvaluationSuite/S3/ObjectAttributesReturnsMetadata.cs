// Translated from behavioral/s3/check_object_attributes_returns_metadata.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-attributes-returns-metadata</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectAttributesReturnsMetadata
{
    /// <summary>
    /// GetObjectAttributes returns exact ETag/size/checksum for a real object, NoSuchKey for a missing one.
    /// </summary>
    [Test]
    [DisplayName("s3.object-attributes-returns-metadata")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "data/payload.bin";
        var body = Enumerable.Repeat((byte)'x', 12345).ToArray();
        var put = await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = new MemoryStream(body),
            ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
        });

        // A single-part, unencrypted upload's ETag is the plain MD5 hex digest of the bytes, and its
        // SHA256 checksum is the plain base64 digest — computed here from `body` independently of
        // anything either AWS response reports, so a target that echoes a self-consistent but wrong
        // value from both PutObject and GetObjectAttributes cannot pass by agreeing with itself.
        var expectedEtag = Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(body));
        var expectedSha256 = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(body));
        var attrs = await Attributes(s3, bucket, key, "ETag", "Checksum", "ObjectSize", "StorageClass");
        Ensure(attrs.ObjectSize == body.Length, $"expected size {body.Length}, got {attrs.ObjectSize}");

        // GetObjectAttributes returns the ETag WITHOUT the surrounding quotes that PutObject's response
        // includes; normalize both sides before comparing.
        Ensure(attrs.ETag.Trim('"') == expectedEtag, $"expected ETag {expectedEtag}, got {attrs.ETag}");
        Ensure(put.ETag.Trim('"') == expectedEtag, $"PutObject's own ETag disagrees: {put.ETag}");
        Ensure(attrs.Checksum?.ChecksumSHA256 == expectedSha256, $"expected checksum {expectedSha256}, got {attrs.Checksum?.ChecksumSHA256}");
        Ensure(put.ChecksumSHA256 == expectedSha256, $"PutObject's own checksum disagrees: {put.ChecksumSHA256}");

        // STANDARD is the default storage class and is often omitted rather than echoed back.
        Ensure(attrs.StorageClass?.Value is null or "STANDARD", "assert attrs.get(\"StorageClass\") in (None, \"STANDARD\")");

        // Positive control confirmed above; now break the one precondition (key existence).
        var (code, message) = await S3Fidelity.Refusal(() => Attributes(s3, bucket, "data/does-not-exist.bin", "ETag"));
        Ensure(code == "NoSuchKey", $"expected NoSuchKey for a missing object, got {code}");
        Ensure(message == "The specified key does not exist.", $"unexpected NoSuchKey message: {Py.Repr(message)}");

        // The forbidden read did not fabricate a result: the real object's attributes are unchanged.
        var attrsAgain = await Attributes(s3, bucket, key, "ETag");
        Ensure(attrsAgain.ETag.Trim('"') == expectedEtag, $"expected ETag {expectedEtag} unchanged after the failed lookup, got {attrsAgain.ETag}");
    }

    private static Task<GetObjectAttributesResponse> Attributes(AmazonS3Client s3, string bucket, string key, params string[] attributes) =>
        s3.GetObjectAttributesAsync(new GetObjectAttributesRequest
        {
            BucketName = bucket,
            Key = key,
            ObjectAttributes = [.. attributes.Select(a => new ObjectAttributes(a))],
        });
}
