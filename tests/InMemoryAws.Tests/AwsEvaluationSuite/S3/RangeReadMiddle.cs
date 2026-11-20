// Translated from behavioral/s3/check_range_read_middle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.range-read-middle</summary>
[Category("AwsEvaluationSuite")]
public sealed class RangeReadMiddle
{
    /// <summary>GetObject with Range='bytes=5-9' returns bytes 5 through 9.</summary>
    [Test]
    [DisplayName("s3.range-read-middle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var body = Encoding.ASCII.GetBytes("0123456789ABCDEF");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "range-mid.bin", InputStream = new MemoryStream(body) });
        using var resp = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "range-mid.bin", ByteRange = new ByteRange("bytes=5-9") });
        var got = Encoding.ASCII.GetString(await S3Fidelity.ReadBody(resp));
        Ensure(got == "56789", $"expected b'56789', got {Py.Repr(got)}");
        Ensure(resp.ContentLength == 5, $"expected ContentLength 5, got {resp.ContentLength}");
        Ensure(resp.HttpStatusCode == HttpStatusCode.PartialContent, $"a satisfiable range must be 206, got {(int)resp.HttpStatusCode}");
    }
}
