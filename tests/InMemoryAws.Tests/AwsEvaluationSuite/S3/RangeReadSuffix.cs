// Translated from behavioral/s3/check_range_read_suffix.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.range-read-suffix</summary>
[Category("AwsEvaluationSuite")]
public sealed class RangeReadSuffix
{
    /// <summary>GetObject with Range='bytes=-4' returns the last 4 bytes.</summary>
    [Test]
    [DisplayName("s3.range-read-suffix")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var body = Encoding.ASCII.GetBytes("0123456789ABCDEF");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "range-sfx.bin", InputStream = new MemoryStream(body) });
        using var resp = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "range-sfx.bin", ByteRange = new ByteRange("bytes=-4") });
        var got = Encoding.ASCII.GetString(await S3Fidelity.ReadBody(resp));
        Ensure(got == "CDEF", $"expected b'CDEF', got {Py.Repr(got)}");
        Ensure(resp.HttpStatusCode == HttpStatusCode.PartialContent, $"a satisfiable suffix range must be 206, got {(int)resp.HttpStatusCode}");
        Ensure(resp.ContentLength == 4, $"expected ContentLength 4, got {resp.ContentLength}");
    }
}
