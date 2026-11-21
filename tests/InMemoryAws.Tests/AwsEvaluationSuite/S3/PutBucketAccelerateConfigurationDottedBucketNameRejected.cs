// Translated from behavioral/s3/check_put_bucket_accelerate_configuration_dotted_bucket_name_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-bucket-accelerate-configuration-dotted-bucket-name-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutBucketAccelerateConfigurationDottedBucketNameRejected
{
    /// <summary>
    /// PutBucketAccelerateConfiguration is rejected on a bucket whose name contains a period,
    /// because Transfer Acceleration requires a DNS-compliant name; a sibling bucket without a
    /// period accepts the identical call.
    /// </summary>
    [Test]
    [DisplayName("s3.put-bucket-accelerate-configuration-dotted-bucket-name-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;

        var dotted = $"{FidelityContext.UniqueName("sim2real-accel-")}.dotted";
        await CreateBucket(ctx, s3, dotted);

        var (code, message) = await S3Fidelity.ErrorPair(
            () => s3.PutBucketAccelerateConfigurationAsync(new PutBucketAccelerateConfigurationRequest
            {
                BucketName = dotted,
                AccelerateConfiguration = new AccelerateConfiguration { Status = BucketAccelerateStatus.Enabled },
            }),
            "expected the call to be rejected, but it succeeded");
        Ensure(code == "InvalidRequest", $"expected InvalidRequest, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(
            message == "S3 Transfer Acceleration is not supported for buckets with periods (.) in their names",
            $"unexpected message {Py.Repr(message)}");

        // effect absent: the rejected PUT never set a status on the dotted bucket
        var unset = await s3.GetBucketAccelerateConfigurationAsync(new GetBucketAccelerateConfigurationRequest { BucketName = dotted });
        Ensure(string.IsNullOrEmpty(unset.Status?.Value), $"expected no Status after the rejected PUT, got {Py.Repr(unset.Status?.Value)}");

        // positive control: the identical call on a sibling bucket with no period is accepted
        var plain = FidelityContext.UniqueName("sim2real-accel-");
        await CreateBucket(ctx, s3, plain);
        await s3.PutBucketAccelerateConfigurationAsync(new PutBucketAccelerateConfigurationRequest
        {
            BucketName = plain,
            AccelerateConfiguration = new AccelerateConfiguration { Status = BucketAccelerateStatus.Enabled },
        });
        var enabled = await s3.GetBucketAccelerateConfigurationAsync(new GetBucketAccelerateConfigurationRequest { BucketName = plain });
        Ensure(enabled.Status == BucketAccelerateStatus.Enabled, $"expected Enabled on the plain bucket, got {Py.Repr(enabled.Status?.Value)}");
    }

    /// <summary><c>_create_bucket(ctx, name)</c>: a bucket of exactly this name, deleted at cleanup.</summary>
    private static async Task CreateBucket(FidelityContext ctx, IAmazonS3 s3, string name)
    {
        var request = new PutBucketRequest { BucketName = name, UseClientRegion = false };
        if (ctx.Region != "us-east-1")
        {
            request.PutBucketConfiguration = new PutBucketConfiguration { LocationConstraint = new BucketLocationConstraint(ctx.Region) };
        }

        await s3.PutBucketAsync(request);
        ctx.OnCleanup(() => S3Fidelity.EmptyAndDelete(s3, name));
    }
}
