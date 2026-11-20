// Translated from behavioral/s3/check_head_object_metadata.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.head-object-metadata</summary>
[Category("AwsEvaluationSuite")]
public sealed class HeadObjectMetadata
{
    /// <summary>HeadObject returns ContentLength, ContentType, and custom metadata.</summary>
    [Test]
    [DisplayName("s3.head-object-metadata")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var body = new byte[42];
        Array.Fill(body, (byte)'x');
        var put = new PutObjectRequest { BucketName = bucket, Key = "meta.bin", InputStream = new MemoryStream(body), ContentType = "application/octet-stream" };
        put.Metadata.Add("sim2real-key", "sim2real-value");
        await s3.PutObjectAsync(put);
        var resp = await s3.GetObjectMetadataAsync(bucket, "meta.bin");
        Ensure(resp.ContentLength == 42, $"expected ContentLength=42, got {resp.ContentLength}");
        Ensure(resp.Headers.ContentType == "application/octet-stream", $"expected ContentType=application/octet-stream, got {resp.Headers.ContentType}");
        var metadata = resp.Metadata.Keys.ToDictionary(k => k, k => resp.Metadata[k], StringComparer.Ordinal);
        Ensure(
            resp.Metadata["sim2real-key"] == "sim2real-value",
            $"expected user metadata sim2real-key=sim2real-value, got {Py.Repr(metadata)}");
        var expectedEtag = $"\"{Convert.ToHexStringLower(MD5.HashData(body))}\"";
        Ensure(resp.ETag == expectedEtag, $"expected ETag {expectedEtag} (MD5 of the unencrypted single-part body), got {resp.ETag}");
    }
}
