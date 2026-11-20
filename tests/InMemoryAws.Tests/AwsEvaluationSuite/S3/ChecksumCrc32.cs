// Translated from behavioral/s3/check_checksum_crc32.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.checksum-crc32</summary>
[Category("AwsEvaluationSuite")]
public sealed class ChecksumCrc32
{
    /// <summary>PutObject with ChecksumAlgorithm=CRC32 stores the client-computed digest; GET returns that value.</summary>
    [Test]
    [DisplayName("s3.checksum-crc32")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var body = Encoding.ASCII.GetBytes("crc32-test-payload");
        var expected = S3Fidelity.Crc32B64(body);

        var put = await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = "crc32-obj",
            InputStream = new MemoryStream(body),
            ChecksumAlgorithm = ChecksumAlgorithm.CRC32,
        });
        Ensure(
            put.ChecksumCRC32 == expected,
            $"PutObject echoed ChecksumCRC32={Py.Repr(put.ChecksumCRC32)}; the client computed {Py.Repr(expected)} with zlib.crc32 over the same {body.Length} bytes");

        using var resp = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "crc32-obj", ChecksumMode = ChecksumMode.ENABLED });
        var crc = resp.ChecksumCRC32;
        Ensure(
            crc == expected,
            $"GetObject with ChecksumMode=ENABLED returned ChecksumCRC32={Py.Repr(crc)}; the client computed {Py.Repr(expected)} with zlib.crc32 over the same {body.Length} bytes");
        var gotBody = await S3Fidelity.ReadBody(resp);
        Ensure(gotBody.AsSpan().SequenceEqual(body), $"GetObject returned {gotBody.Length} bytes, want {body.Length}");
        Ensure(
            S3Fidelity.Crc32B64(gotBody) == crc,
            $"the bytes S3 returned hash to {Py.Repr(S3Fidelity.Crc32B64(gotBody))} but S3 reported ChecksumCRC32={Py.Repr(crc)}");
    }
}
