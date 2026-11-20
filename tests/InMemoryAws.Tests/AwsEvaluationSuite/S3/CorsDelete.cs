// Translated from behavioral/s3/check_cors_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsDelete
{
    /// <summary>
    /// DeleteBucketCors removes CORS config; subsequent GET fails with NoSuchCORSConfiguration.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        (string? Code, string? Message) noCors = ("NoSuchCORSConfiguration", "The CORS configuration does not exist");
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration { Rules = [new CORSRule { AllowedMethods = ["GET"], AllowedOrigins = ["*"] }] },
        });

        // positive control: the config is actually there before we delete it
        var before = await s3.GetCORSConfigurationAsync(new GetCORSConfigurationRequest { BucketName = bucket });
        Ensure(
            before.Configuration.Rules[0].AllowedOrigins.SequenceEqual(["*"]),
            $"expected the CORS rule just written, got {Py.Repr(before.Configuration.Rules.Count)} rule(s)");

        await s3.DeleteCORSConfigurationAsync(new DeleteCORSConfigurationRequest { BucketName = bucket });
        var gone = await S3Fidelity.Refusal(() => s3.GetCORSConfigurationAsync(new GetCORSConfigurationRequest { BucketName = bucket }));
        Ensure(gone == noCors, $"expected {S3Fidelity.Repr(noCors)} after CORS deletion, got {S3Fidelity.Repr(gone)}");
    }
}
