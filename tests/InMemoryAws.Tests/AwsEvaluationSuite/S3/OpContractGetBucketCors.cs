// Translated from behavioral/s3/check_op_contract_get_bucket_cors.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-get-bucket-cors</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractGetBucketCors
{
    /// <summary>
    /// GetBucketCors returns the configured CORS rules; a bucket with no CORS config raises NoSuchCORSConfiguration.
    /// </summary>
    [Test]
    [DisplayName("s3.op-contract-get-bucket-cors")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");

        // --- error boundary first: fresh bucket has no CORS configuration ---
        var (code, message) = await ClientError(() => s3.GetCORSConfigurationAsync(new GetCORSConfigurationRequest { BucketName = bucket }));
        Ensure(code == "NoSuchCORSConfiguration", $"expected NoSuchCORSConfiguration, got {Py.Repr(code)}");
        Ensure(message == "The CORS configuration does not exist", $"unexpected message {Py.Repr(message)}");

        // --- success path: put rules, read them back verbatim ---
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration
            {
                Rules = [new CORSRule { AllowedMethods = ["GET", "PUT"], AllowedOrigins = ["https://app.example.com"], AllowedHeaders = ["*"], MaxAgeSeconds = 3000 }],
            },
        });
        var got = await s3.GetCORSConfigurationAsync(new GetCORSConfigurationRequest { BucketName = bucket });
        var rule = got.Configuration.Rules[0];
        var methods = rule.AllowedMethods.Order(StringComparer.Ordinal).ToList();
        Ensure(methods.SequenceEqual(["GET", "PUT"]), $"expected AllowedMethods ['GET', 'PUT'], got {Py.Repr(methods)}");
        Ensure(rule.AllowedOrigins.SequenceEqual(["https://app.example.com"]), $"expected AllowedOrigins ['https://app.example.com'], got {Py.Repr(rule.AllowedOrigins)}");
        Ensure(rule.MaxAgeSeconds == 3000, $"expected MaxAgeSeconds 3000, got {rule.MaxAgeSeconds}");
    }

    /// <summary>
    /// The (Error.Code, Error.Message) of fn(); a code-less HEAD error yields the HTTP status. Raises
    /// AssertionError if fn succeeds.
    /// </summary>
    private static Task<(string? Code, string? Message)> ClientError(Func<Task> fn) =>
        S3Fidelity.ClientError(fn, "expected a ClientError, but the call SUCCEEDED");
}
