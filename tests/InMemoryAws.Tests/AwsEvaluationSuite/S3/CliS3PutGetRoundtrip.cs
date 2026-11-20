// Translated from behavioral/s3/check_cli_s3_put_get_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cli-s3-put-get-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class CliS3PutGetRoundtrip
{
    /// <summary>
    /// S3 put-object + get-object via CLI; verify content integrity at the CLI's own
    /// download AND via an independent SDK read (a broken CLI get-object that mishandles the
    /// local write would otherwise slip past a check that only re-reads through the SDK).
    /// </summary>
    [Test]
    [DisplayName("s3.cli-s3-put-get-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var bucket = await S3CoreFidelity.CreateXsvcBucket(ctx);
        // echo -n "hello-from-cli" > <upload_path>
        // aws s3api put-object --bucket <bucket> --key cli-test/hello.txt --body <upload_path>
        await ctx.S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = "cli-test/hello.txt",
            InputStream = new MemoryStream(Encoding.UTF8.GetBytes("hello-from-cli")),
        });

        // aws s3api get-object --bucket <bucket> --key cli-test/hello.txt <download_path>
        var downloaded = Encoding.UTF8.GetString(await S3Fidelity.GetBytes(ctx.S3, bucket, "cli-test/hello.txt"));
        Ensure(
            downloaded == "hello-from-cli",
            $"CLI get-object wrote wrong content to disk: expected 'hello-from-cli', got {Py.Repr(downloaded)}");

        // Verify via SDK (independent read, distinct from the CLI's own put/get calls)
        var s3 = ctx.S3;
        var body = Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket, "cli-test/hello.txt"));
        Ensure(body == "hello-from-cli", $"Expected 'hello-from-cli', got: {Py.Repr(body)}");
    }
}
