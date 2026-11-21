// Translated from behavioral/s3/check_put_bucket_accelerate_configuration_directory_bucket_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-bucket-accelerate-configuration-directory-bucket-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutBucketAccelerateConfigurationDirectoryBucketRejected
{
    /// <summary>
    /// S3 Express One Zone directory buckets are Zonal: the AZ segment is part of the bucket name
    /// and must be a real AZ ID in the region under test.
    /// </summary>
    private static readonly Dictionary<string, string[]> S3ExpressAzByRegion = new(StringComparer.Ordinal)
    {
        ["us-east-1"] = ["use1-az4", "use1-az5", "use1-az6"],
        ["us-east-2"] = ["use2-az1", "use2-az2"],
        ["us-west-2"] = ["usw2-az1", "usw2-az3", "usw2-az4"],
        ["ap-south-1"] = ["aps1-az1", "aps1-az3"],
        ["ap-northeast-1"] = ["apne1-az1", "apne1-az4"],
        ["eu-central-1"] = ["euc1-az1", "euc1-az2"],
        ["eu-west-1"] = ["euw1-az1", "euw1-az3"],
        ["eu-north-1"] = ["eun1-az1", "eun1-az2", "eun1-az3"],
    };

    /// <summary>
    /// PutBucketAccelerateConfiguration is rejected on a directory bucket, which has no
    /// Transfer Acceleration concept; the identical call on a general purpose bucket is accepted.
    /// </summary>
    [Test]
    [DisplayName("s3.put-bucket-accelerate-configuration-directory-bucket-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);

        // The .NET SDK serves a directory bucket's control calls without an S3 Express session, as
        // botocore does with disable_s3_express_session_auth.
        var control = ctx.S3;

        var azs = S3ExpressAzByRegion.TryGetValue(ctx.Region, out var zones) ? zones : throw Fail($"no known S3 Express One Zone AZ id for region {Py.Repr(ctx.Region)}");
        var baseName = FidelityContext.UniqueName("sim2real-accdir-").ToLowerInvariant().TrimEnd('-');
        var (directoryBucket, _) = await S3Fidelity.CreateDirectoryBucket(ctx, control, baseName, azs);

        var (code, message) = await S3Fidelity.ErrorPair(
            () => control.PutBucketAccelerateConfigurationAsync(new PutBucketAccelerateConfigurationRequest
            {
                BucketName = directoryBucket,
                AccelerateConfiguration = new AccelerateConfiguration { Status = BucketAccelerateStatus.Enabled },
            }),
            "expected the call to be rejected, but it succeeded");
        Ensure(code == "InvalidArgument", $"expected InvalidArgument, got {Py.Repr(code)}: {Py.Repr(message)}");
        Ensure(message == "AccelerateConfiguration is not valid, expected CreateBucketConfiguration", $"unexpected message {Py.Repr(message)}");

        // effect absent: a directory bucket carries no accelerate status at all
        var (unsetCode, unsetMessage) = await S3Fidelity.ErrorPair(
            () => control.GetBucketAccelerateConfigurationAsync(new GetBucketAccelerateConfigurationRequest { BucketName = directoryBucket }),
            "expected the call to be rejected, but it succeeded");
        Ensure(unsetCode == "MethodNotAllowed", $"expected GetBucketAccelerateConfiguration to be refused, got {Py.Repr(unsetCode)}: {Py.Repr(unsetMessage)}");
        Ensure(unsetMessage == "The specified method is not allowed against this resource.", $"unexpected message {Py.Repr(unsetMessage)}");

        // positive control: the identical PutBucketAccelerateConfiguration on a general purpose
        // bucket is accepted and reads back through GetBucketAccelerateConfiguration.
        var generalBucket = await S3Fidelity.CreateBucket(ctx, "sim2real-accgp-", control);
        await control.PutBucketAccelerateConfigurationAsync(new PutBucketAccelerateConfigurationRequest
        {
            BucketName = generalBucket,
            AccelerateConfiguration = new AccelerateConfiguration { Status = BucketAccelerateStatus.Enabled },
        });
        var enabled = await control.GetBucketAccelerateConfigurationAsync(new GetBucketAccelerateConfigurationRequest { BucketName = generalBucket });
        Ensure(enabled.Status == BucketAccelerateStatus.Enabled, $"expected Enabled on the general purpose bucket, got {Py.Repr(enabled.Status?.Value)}");
    }
}
