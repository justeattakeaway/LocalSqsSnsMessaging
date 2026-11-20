// Translated from behavioral/s3/check_cli_s3_get_nonexistent_key_error.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cli-s3-get-nonexistent-key-error</summary>
[Category("AwsEvaluationSuite")]
public sealed class CliS3GetNonexistentKeyError
{
    /// <summary>
    /// Getting a non-existent S3 key via CLI must return NoSuchKey error, while an existing key in
    /// the same bucket must still be readable — without the positive control, a CLI/target that fails
    /// every get-object call would pass this check for the wrong reason.
    /// </summary>
    [Test]
    [DisplayName("s3.cli-s3-get-nonexistent-key-error")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var bucket = await S3CoreFidelity.CreateXsvcBucket(ctx);
        var s3 = ctx.S3;
        const string existingKey = "exists/key.txt";
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = existingKey,
            InputStream = new MemoryStream(Encoding.UTF8.GetBytes("positive-control")),
        });

        // `aws s3api get-object --bucket <bucket> --key exists/key.txt /dev/null` is this GetObject,
        // its body written away; the CLI exits non-zero, printing the error, when it fails.
        var ok = await FidelityContext.CaptureError(() => S3Fidelity.GetBytes(s3, bucket, existingKey));
        Ensure(
            ok is null,
            $"Positive control failed: get-object on an existing key should succeed, stderr={ok}");

        // `aws s3api get-object --bucket <bucket> --key nonexistent/key.txt /dev/null`
        var err = await FidelityContext.CaptureError(() => S3Fidelity.GetBytes(s3, bucket, "nonexistent/key.txt"));
        Ensure(err is not null, "Expected failure for non-existent key");
        // The CLI prints "An error occurred (<Code>) when calling the GetObject operation: <Message>".
        var stderr = $"An error occurred ({err.Code}) when calling the GetObject operation: {err.Message}";
        Ensure(
            stderr.Contains("NoSuchKey", StringComparison.Ordinal) || stderr.Contains("Not Found", StringComparison.Ordinal) || stderr.Contains("404", StringComparison.Ordinal),
            $"Expected NoSuchKey/Not Found/404 in stderr, got: {stderr}");
    }
}
