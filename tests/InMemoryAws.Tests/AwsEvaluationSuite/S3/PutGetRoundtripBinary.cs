// Translated from behavioral/s3/check_put_get_roundtrip_binary.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-get-roundtrip-binary</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutGetRoundtripBinary
{
    private const string Check = "put-get-roundtrip-binary";

    /// <summary>Binary data round-trips byte-exact (all 256 byte values).</summary>
    [Test]
    [DisplayName("s3.put-get-roundtrip-binary")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check);
        // 25.6 KB of all byte values
        var body = Enumerable.Repeat(Enumerable.Range(0, 256).Select(i => (byte)i), 100).SelectMany(b => b).ToArray();
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "binary-blob", InputStream = new MemoryStream(body) });
        var got = await S3Fidelity.GetBytes(s3, bucket, "binary-blob");
        if (!got.AsSpan().SequenceEqual(body))
        {
            var n = Math.Min(got.Length, body.Length);
            var idx = Enumerable.Range(0, n).FirstOrDefault(i => got[i] != body[i], n);
            throw Fail(
                $"binary roundtrip failed: len(got)={got.Length} vs len(body)={body.Length}, "
                + $"first mismatch at byte {idx}: got={Convert.ToHexString(got.AsSpan(idx, Math.Min(8, got.Length - idx)))} "
                + $"expected={Convert.ToHexString(body.AsSpan(idx, Math.Min(8, body.Length - idx)))}");
        }
    }
}
