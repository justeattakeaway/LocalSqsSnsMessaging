// Translated from behavioral/s3/check_content_encoding_preserved.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.content-encoding-preserved</summary>
[Category("AwsEvaluationSuite")]
public sealed class ContentEncodingPreserved
{
    /// <summary>ContentEncoding header is preserved on retrieval.</summary>
    [Test]
    [DisplayName("s3.content-encoding-preserved")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var body = new byte[22];
        body[0] = 0x1f;
        body[1] = 0x8b;
        var put = new PutObjectRequest { BucketName = bucket, Key = "compressed.gz", InputStream = new MemoryStream(body) };
        put.Headers.ContentEncoding = "gzip";
        await s3.PutObjectAsync(put);
        using var resp = await s3.GetObjectAsync(bucket, "compressed.gz");
        var encoding = resp.Headers.ContentEncoding;
        Ensure(encoding == "gzip", $"expected ContentEncoding gzip, got {Py.Repr(encoding)}");
    }
}
