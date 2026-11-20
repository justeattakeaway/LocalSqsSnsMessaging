// Translated from behavioral/s3/check_lifecycle_expiration_ttl.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.lifecycle-expiration-ttl</summary>
/// <remarks>
/// boto3 hands the check the raw <c>x-amz-expiration</c> header as <c>Expiration</c>; the .NET SDK
/// parses it, so the port reads the raw header from the response.
/// </remarks>
[Category("AwsEvaluationSuite")]
public sealed class LifecycleExpirationTtl
{
    /// <summary>
    /// An object matched by an Expiration lifecycle rule carries an x-amz-expiration header naming the
    /// rule with an expiry date in the expected window on HeadObject and GetObject, while an object outside
    /// the rule's filter carries none.
    /// </summary>
    [Test]
    [DisplayName("s3.lifecycle-expiration-ttl")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new LifecycleConfiguration
            {
                Rules =
                [
                    new LifecycleRule
                    {
                        Id = "sim2real-ttl-expire-rule",
                        Filter = new LifecycleFilter { Prefix = "ttl-" },
                        Status = LifecycleRuleStatus.Enabled,
                        Expiration = new LifecycleRuleExpiration { Days = 5 },
                    },
                ],
            },
        });

        var cfg = await s3.GetLifecycleConfigurationAsync(new GetLifecycleConfigurationRequest { BucketName = bucket });
        var ruleIds = (cfg.Configuration?.Rules ?? []).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        Ensure(ruleIds.Contains("sim2real-ttl-expire-rule"), $"expected the rule reflected in GetBucketLifecycleConfiguration, got {Py.Repr(ruleIds.ToList())}");

        const string key = "ttl-expiring-object.txt";
        var beforePut = FidelityTime.UtcNow;
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "lifecycle-ttl-payload" });

        var (_, head) = await S3Fidelity.WithHeaders(s3, () => s3.GetObjectMetadataAsync(bucket, key));
        var expiration = head.GetValueOrDefault("x-amz-expiration");
        Ensure(expiration is not null, "expected an x-amz-expiration header on an object matched by an Expiration rule");
        Ensure(expiration.Contains("sim2real-ttl-expire-rule", StringComparison.Ordinal), $"expected the matching rule-id in the Expiration header, got {Py.Repr(expiration)}");

        // S3 rounds the expiration to midnight UTC on the day the Days period elapses, so the
        // earliest it can land is Days after creation and the latest is Days+1 (the rounding-up slop).
        var expiry = S3Fidelity.ExpiryDate(expiration);
        var earliest = beforePut + TimeSpan.FromDays(5);
        var latest = beforePut + TimeSpan.FromDays(6);
        Ensure(
            earliest <= expiry && expiry <= latest,
            $"expected the expiry-date to fall {5}-6 days after object creation ({beforePut:O}), got {expiry:O}");

        var (got, gotHeaders) = await S3Fidelity.WithHeaders(s3, () => s3.GetObjectAsync(bucket, key));
        using (got)
        {
            Ensure(gotHeaders.GetValueOrDefault("x-amz-expiration") is not null, "expected the Expiration header on GetObject too");
            Ensure((await S3Fidelity.ReadBody(got)).AsSpan().SequenceEqual("lifecycle-ttl-payload"u8), "assert got[\"Body\"].read() == b\"lifecycle-ttl-payload\"");
        }

        // An object that does NOT match the rule's prefix filter is the control: it must carry no
        // Expiration header at all, proving the target applies the filter rather than stamping every
        // object in a bucket that merely has a lifecycle rule enabled.
        const string otherKey = "no-match-object.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = otherKey, ContentBody = "unrelated-payload" });
        var (_, otherHead) = await S3Fidelity.WithHeaders(s3, () => s3.GetObjectMetadataAsync(bucket, otherKey));
        Ensure(
            otherHead.GetValueOrDefault("x-amz-expiration") is null,
            $"expected no Expiration header on an object outside the rule's prefix filter, got {Py.Repr(otherHead.GetValueOrDefault("x-amz-expiration"))}");
        var (otherGot, otherGotHeaders) = await S3Fidelity.WithHeaders(s3, () => s3.GetObjectAsync(bucket, otherKey));
        otherGot.Dispose();
        Ensure(
            otherGotHeaders.GetValueOrDefault("x-amz-expiration") is null,
            $"expected no Expiration header on GetObject for an object outside the rule's prefix filter, got {Py.Repr(otherGotHeaders.GetValueOrDefault("x-amz-expiration"))}");
    }
}
