// Translated from behavioral/s3/check_range_read_open_end.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.range-read-open-end</summary>
[Category("AwsEvaluationSuite")]
public sealed class RangeReadOpenEnd
{
    /// <summary>GetObject with Range='bytes=10-' returns from byte 10 to end.</summary>
    [Test]
    [DisplayName("s3.range-read-open-end")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var body = Encoding.ASCII.GetBytes("0123456789ABCDEF");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "range-open.bin", InputStream = new MemoryStream(body) });
        using var resp = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "range-open.bin", ByteRange = new ByteRange("bytes=10-") });
        var got = Encoding.ASCII.GetString(await S3Fidelity.ReadBody(resp));
        Ensure(got == "ABCDEF", $"expected b'ABCDEF', got {Py.Repr(got)}");
        Ensure(resp.HttpStatusCode == HttpStatusCode.PartialContent, $"an open-ended satisfiable range must be 206, got {(int)resp.HttpStatusCode}");
        Ensure(resp.ContentLength == 6, $"expected ContentLength 6, got {resp.ContentLength}");
        Ensure(resp.ContentRange == $"bytes 10-{body.Length - 1}/{body.Length}", $"unexpected ContentRange: {Py.Repr(resp.ContentRange)}");
    }
}
