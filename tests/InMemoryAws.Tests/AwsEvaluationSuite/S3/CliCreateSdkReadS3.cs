// Translated from behavioral/s3/check_cli_create_sdk_read_s3.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cli-create-sdk-read-s3</summary>
[Category("AwsEvaluationSuite")]
public sealed class CliCreateSdkReadS3
{
    /// <summary>Upload object via CLI, read via SDK.</summary>
    [Test]
    [DisplayName("s3.cli-create-sdk-read-s3")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var bucket = await S3CoreFidelity.CreateXsvcBucket(ctx);
        // echo -n "cli-uploaded" > /tmp/sim2real-interop-upload.txt
        // aws s3api put-object --bucket <bucket> --key interop/test.txt --body /tmp/sim2real-interop-upload.txt
        await ctx.S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = "interop/test.txt",
            InputStream = new MemoryStream(Encoding.UTF8.GetBytes("cli-uploaded")),
        });

        var s3 = ctx.S3;
        var body = Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket, "interop/test.txt"));
        Ensure(body == "cli-uploaded", $"Expected 'cli-uploaded', got {Py.Repr(body)}");
    }
}
