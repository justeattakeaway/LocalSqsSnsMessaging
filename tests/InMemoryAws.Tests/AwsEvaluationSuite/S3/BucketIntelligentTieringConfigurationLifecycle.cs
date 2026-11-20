// Translated from behavioral/s3/check_bucket_intelligent_tiering_configuration_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-intelligent-tiering-configuration-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketIntelligentTieringConfigurationLifecycle
{
    /// <summary>Put creates a named Intelligent-Tiering config visible via List/Get; Delete removes it from both.</summary>
    [Test]
    [DisplayName("s3.bucket-intelligent-tiering-configuration-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-tier-");
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = bucket, TagSet = [new Tag { Key = "sim2real", Value = "true" }] });

        var configId = FidelityContext.UniqueName("tiercfg-");
        List<Tiering> Tierings() =>
        [
            new Tiering { Days = 90, AccessTier = IntelligentTieringAccessTier.ARCHIVE_ACCESS },
            new Tiering { Days = 180, AccessTier = IntelligentTieringAccessTier.DEEP_ARCHIVE_ACCESS },
        ];
        await s3.PutBucketIntelligentTieringConfigurationAsync(new PutBucketIntelligentTieringConfigurationRequest
        {
            BucketName = bucket,
            IntelligentTieringId = configId,
            IntelligentTieringConfiguration = new IntelligentTieringConfiguration
            {
                IntelligentTieringId = configId,
                Status = IntelligentTieringStatus.Enabled,
                Tierings = Tierings(),
            },
        });

        var got = await s3.GetBucketIntelligentTieringConfigurationAsync(new GetBucketIntelligentTieringConfigurationRequest { BucketName = bucket, IntelligentTieringId = configId });
        var cfg = got.IntelligentTieringConfiguration;
        Ensure(cfg.IntelligentTieringId == configId, $"expected Id {Py.Repr(configId)}, got {Py.Repr(cfg.IntelligentTieringId)}");
        Ensure(cfg.Status == "Enabled", $"expected Status Enabled, got {Py.Repr(cfg.Status?.Value)}");
        var tierings = Boto(Tierings());
        var gotTierings = Boto(cfg.Tierings ?? []);
        Ensure(Py.Equal(gotTierings, tierings), $"expected Tierings {Py.Repr(tierings)} preserved verbatim, got {Py.Repr(gotTierings)}");

        var listed = await s3.ListBucketIntelligentTieringConfigurationsAsync(new ListBucketIntelligentTieringConfigurationsRequest { BucketName = bucket });
        var ids = (listed.IntelligentTieringConfigurationList ?? []).Select(c => c.IntelligentTieringId).ToList();
        Ensure(ids.SequenceEqual([configId]), $"expected exactly [{Py.Repr(configId)}] listed, got {Py.Repr(ids)}");

        await s3.DeleteBucketIntelligentTieringConfigurationAsync(new DeleteBucketIntelligentTieringConfigurationRequest { BucketName = bucket, IntelligentTieringId = configId });

        // The .NET SDK answers GetBucketIntelligentTieringConfiguration's 404 as an empty success; the refusal is read off the wire.
        var (code, message) = S3Fidelity.Botocore(await S3Fidelity.RawError(ctx, "GET", $"{bucket}?intelligent-tiering&id={configId}", "expected the call to be rejected, but it SUCCEEDED"));
        Ensure(code == "NoSuchConfiguration", $"expected NoSuchConfiguration after delete, got {code}");
        const string expected = "The specified configuration does not exist.";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");

        var listedAfter = await s3.ListBucketIntelligentTieringConfigurationsAsync(new ListBucketIntelligentTieringConfigurationsRequest { BucketName = bucket });
        var idsAfter = (listedAfter.IntelligentTieringConfigurationList ?? []).Select(c => c.IntelligentTieringId).ToList();
        Ensure(!idsAfter.Contains(configId), $"config {configId} still listed after delete: {Py.Repr(idsAfter)}");
    }

    private static List<Dictionary<string, object?>> Boto(IEnumerable<Tiering> tierings) =>
        [.. tierings.Select(t => new Dictionary<string, object?>(StringComparer.Ordinal) { ["Days"] = t.Days, ["AccessTier"] = t.AccessTier?.Value })];
}
