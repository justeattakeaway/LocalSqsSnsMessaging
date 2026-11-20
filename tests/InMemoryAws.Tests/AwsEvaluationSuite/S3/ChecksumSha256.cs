// Translated from behavioral/s3/check_checksum_sha256.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.checksum-sha256</summary>
[Category("AwsEvaluationSuite")]
public sealed class ChecksumSha256
{
    /// <summary>PutObject with ChecksumAlgorithm=SHA256 returns the true SHA256 of the body on GET.</summary>
    [Test]
    [DisplayName("s3.checksum-sha256")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var body = Encoding.ASCII.GetBytes("sha256-test-payload");
        var expected = S3Fidelity.Sha256B64(body);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = "sha256-obj",
            InputStream = new MemoryStream(body),
            ChecksumAlgorithm = ChecksumAlgorithm.SHA256,
        });
        using var resp = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "sha256-obj", ChecksumMode = ChecksumMode.ENABLED });
        var sha = resp.ChecksumSHA256;
        Ensure(sha == expected, $"ChecksumSHA256 mismatch: expected {Py.Repr(expected)} (real SHA256 of body), got {Py.Repr(sha)}");
    }
}
