// Translated from behavioral/s3/check_bucket_inventory_configuration_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-inventory-configuration-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketInventoryConfigurationRoundtrip
{
    /// <summary>PutBucketInventoryConfiguration entries appear in list/get and disappear after delete.</summary>
    [Test]
    [DisplayName("s3.bucket-inventory-configuration-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var accountId = ctx.Account;
        const string cfgId = "sim2real-inventory";
        var config = new InventoryConfiguration
        {
            InventoryId = cfgId,
            IsEnabled = true,
            IncludedObjectVersions = InventoryIncludedObjectVersions.Current,
            Schedule = new InventorySchedule { Frequency = InventoryFrequency.Daily },
            Destination = new InventoryDestination
            {
                S3BucketDestination = new InventoryS3BucketDestination
                {
                    AccountId = accountId,
                    BucketName = $"arn:aws:s3:::{bucket}",
                    InventoryFormat = InventoryFormat.CSV,
                    Prefix = "inventory-reports",
                },
            },
        };

        await s3.PutBucketInventoryConfigurationAsync(new PutBucketInventoryConfigurationRequest { BucketName = bucket, InventoryId = cfgId, InventoryConfiguration = config });
        var fetched = (await s3.GetBucketInventoryConfigurationAsync(new GetBucketInventoryConfigurationRequest { BucketName = bucket, InventoryId = cfgId })).InventoryConfiguration;
        Ensure(fetched.InventoryId == cfgId, "assert fetched[\"Id\"] == cfg_id");
        Ensure(fetched.IsEnabled == true, "assert fetched[\"IsEnabled\"] is True");

        var listed = await s3.ListBucketInventoryConfigurationsAsync(new ListBucketInventoryConfigurationsRequest { BucketName = bucket });
        var ids = (listed.InventoryConfigurationList ?? []).Select(c => c.InventoryId).ToHashSet(StringComparer.Ordinal);
        Ensure(ids.Contains(cfgId), $"expected {cfgId} in ListBucketInventoryConfigurations, got {Py.Repr(ids.ToList())}");

        await s3.DeleteBucketInventoryConfigurationAsync(new DeleteBucketInventoryConfigurationRequest { BucketName = bucket, InventoryId = cfgId });
        // The .NET SDK answers GetBucketInventoryConfiguration's 404 as an empty success; the refusal is read off the wire.
        var (code, message) = S3Fidelity.Botocore(await S3Fidelity.RawError(ctx, "GET", $"{bucket}?inventory&id={cfgId}"));
        Ensure(code == "NoSuchConfiguration", $"expected NoSuchConfiguration after delete, got {code}");
        Ensure(message == "The specified configuration does not exist.", message ?? "None");
        var listedAfter = await s3.ListBucketInventoryConfigurationsAsync(new ListBucketInventoryConfigurationsRequest { BucketName = bucket });
        var idsAfter = (listedAfter.InventoryConfigurationList ?? []).Select(c => c.InventoryId).ToHashSet(StringComparer.Ordinal);
        Ensure(!idsAfter.Contains(cfgId), "deleted inventory config must disappear from the list");
    }
}
