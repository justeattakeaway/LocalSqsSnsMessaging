// Translated from behavioral/s3/check_bucket_inventory_configuration_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-inventory-configuration-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketInventoryConfigurationLifecycle
{
    private const string NoSuchConfigurationMessage = "The specified configuration does not exist.";

    /// <summary>Put creates a named inventory config visible via List/Get; Delete removes it from both.</summary>
    [Test]
    [DisplayName("s3.bucket-inventory-configuration-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var accountId = ctx.Account;
        var srcBucket = await S3Fidelity.CreateBucket(ctx, "sim2real-inv-src-");
        var dstBucket = await S3Fidelity.CreateBucket(ctx, "sim2real-inv-dst-");

        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "InventoryDestPolicy",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "s3.amazonaws.com" },
                ["Action"] = "s3:PutObject",
                ["Resource"] = $"arn:aws:s3:::{dstBucket}/*",
                ["Condition"] = new JsonObject
                {
                    ["ArnLike"] = new JsonObject { ["aws:SourceArn"] = $"arn:aws:s3:::{srcBucket}" },
                    ["StringEquals"] = new JsonObject
                    {
                        ["aws:SourceAccount"] = accountId,
                        ["s3:x-amz-acl"] = "bucket-owner-full-control",
                    },
                },
            }),
        };
        await s3.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = dstBucket, Policy = policy.ToJsonString() });

        var invId = FidelityContext.UniqueName("invcfg-");
        await s3.PutBucketInventoryConfigurationAsync(new PutBucketInventoryConfigurationRequest
        {
            BucketName = srcBucket,
            InventoryId = invId,
            InventoryConfiguration = new InventoryConfiguration
            {
                Destination = new InventoryDestination
                {
                    S3BucketDestination = new InventoryS3BucketDestination
                    {
                        BucketName = $"arn:aws:s3:::{dstBucket}",
                        InventoryFormat = InventoryFormat.CSV,
                    },
                },
                IsEnabled = true,
                InventoryId = invId,
                IncludedObjectVersions = InventoryIncludedObjectVersions.Current,
                Schedule = new InventorySchedule { Frequency = InventoryFrequency.Daily },
            },
        });

        var got = await s3.GetBucketInventoryConfigurationAsync(new GetBucketInventoryConfigurationRequest { BucketName = srcBucket, InventoryId = invId });
        Ensure(got.InventoryConfiguration.InventoryId == invId, "assert got[\"InventoryConfiguration\"][\"Id\"] == inv_id");
        Ensure(got.InventoryConfiguration.IsEnabled == true, "assert got[\"InventoryConfiguration\"][\"IsEnabled\"] is True");

        var ids = await ListIds(s3, srcBucket);
        Ensure(ids.Contains(invId), $"expected {invId} in [{string.Join(", ", ids.Select(Py.Repr))}]");

        await s3.DeleteBucketInventoryConfigurationAsync(new DeleteBucketInventoryConfigurationRequest { BucketName = srcBucket, InventoryId = invId });
        // The .NET SDK answers this 404 as an empty success; the refusal is read off the wire.
        var error = await S3Fidelity.RawError(ctx, "GET", $"{srcBucket}?inventory&id={invId}", "expected the call to be rejected, but it SUCCEEDED");
        Ensure(error.Code == "NoSuchConfiguration", $"expected NoSuchConfiguration after delete, got {error.Code}");
        Ensure(error.Message == NoSuchConfigurationMessage, $"unexpected message {Py.Repr(error.Message)}");
        var idsAfter = await ListIds(s3, srcBucket);
        Ensure(!idsAfter.Contains(invId), $"config {invId} still listed after delete: [{string.Join(", ", idsAfter.Select(Py.Repr))}]");

        // Measured, not assumed: unlike CloudWatch's mostly-idempotent delete family,
        // a second DeleteBucketInventoryConfiguration on an already-gone id enforces a
        // genuine not-found precondition rather than a silent no-op.
        var redelete = await S3Fidelity.Error(
            () => s3.DeleteBucketInventoryConfigurationAsync(new DeleteBucketInventoryConfigurationRequest { BucketName = srcBucket, InventoryId = invId }),
            "expected the call to be rejected, but it SUCCEEDED");
        Ensure(redelete.Code == "NoSuchConfiguration", $"expected a second delete to report NoSuchConfiguration, got {redelete.Code}");
        Ensure(redelete.Message == NoSuchConfigurationMessage, $"unexpected message {Py.Repr(redelete.Message)}");
        var stillGone = await ListIds(s3, srcBucket);
        Ensure(!stillGone.Contains(invId), $"double-delete resurrected {invId}: [{string.Join(", ", stillGone.Select(Py.Repr))}]");
    }

    private static async Task<List<string>> ListIds(IAmazonS3 s3, string bucket) =>
        [.. ((await s3.ListBucketInventoryConfigurationsAsync(new ListBucketInventoryConfigurationsRequest { BucketName = bucket })).InventoryConfigurationList ?? [])
            .Select(c => c.InventoryId)];
}
