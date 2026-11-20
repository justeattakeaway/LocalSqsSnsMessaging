// Translated from behavioral/s3/check_range_read_byte_range.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.range-read-byte-range</summary>
[Category("AwsEvaluationSuite")]
public sealed class RangeReadByteRange
{
    /// <summary>GetObject with Range='bytes=0-4' returns the first 5 bytes.</summary>
    [Test]
    [DisplayName("s3.range-read-byte-range")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", "behavioral");
        var body = Encoding.ASCII.GetBytes("0123456789ABCDEF");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "range.bin", InputStream = new MemoryStream(body) });
        using var resp = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "range.bin", ByteRange = new ByteRange("bytes=0-4") });
        var got = Encoding.ASCII.GetString(await S3Fidelity.ReadBody(resp));
        Ensure(got == "01234", $"expected b'01234', got {Py.Repr(got)}");
        Ensure(resp.ContentLength == 5, $"expected ContentLength 5, got {resp.ContentLength}");
        var status = (int)resp.HttpStatusCode;
        Ensure(resp.HttpStatusCode == HttpStatusCode.PartialContent, $"expected HTTP 206 Partial Content, got {status}");
    }
}
