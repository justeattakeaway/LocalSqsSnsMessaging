// Translated from behavioral/s3/check_op_contract_delete_bucket_cors.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-delete-bucket-cors</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractDeleteBucketCors
{
    /// <summary>
    /// DeleteBucketCors clears the CORS configuration, so GetBucketCors then fails.
    ///
    /// A missing bucket is rejected with NoSuchBucket.
    /// </summary>
    [Test]
    [DisplayName("s3.op-contract-delete-bucket-cors")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");

        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration
            {
                Rules = [new CORSRule { AllowedMethods = ["GET"], AllowedOrigins = ["https://example.com"], AllowedHeaders = ["*"] }],
            },
        });
        var got = await s3.GetCORSConfigurationAsync(new GetCORSConfigurationRequest { BucketName = bucket });
        Ensure(
            got.Configuration.Rules[0].AllowedOrigins.SequenceEqual(["https://example.com"]),
            $"expected AllowedOrigins=['https://example.com'], got {Py.Repr(got.Configuration.Rules[0].AllowedOrigins)}");

        await s3.DeleteCORSConfigurationAsync(new DeleteCORSConfigurationRequest { BucketName = bucket });
        var (code, message) = await ClientError(() => s3.GetCORSConfigurationAsync(new GetCORSConfigurationRequest { BucketName = bucket }));
        Ensure(code == "NoSuchCORSConfiguration", $"expected NoSuchCORSConfiguration, got {Py.Repr(code)}");
        Ensure(message == "The CORS configuration does not exist", $"unexpected message {Py.Repr(message)}");

        var ghostBucket = FidelityContext.UniqueName("sim2real-behav-ghost-");
        (code, message) = await ClientError(() => s3.DeleteCORSConfigurationAsync(new DeleteCORSConfigurationRequest { BucketName = ghostBucket }));
        Ensure(code == "NoSuchBucket", $"expected NoSuchBucket, got {Py.Repr(code)}");
        Ensure(message == "The specified bucket does not exist", $"unexpected message {Py.Repr(message)}");
    }

    /// <summary>
    /// The (Error.Code, Error.Message) of fn(); a code-less HEAD error yields the HTTP status. Raises
    /// AssertionError if fn succeeds.
    /// </summary>
    private static Task<(string? Code, string? Message)> ClientError(Func<Task> fn) =>
        S3Fidelity.ClientError(fn, "expected a ClientError, but the call SUCCEEDED");
}
