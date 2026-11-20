// Translated from behavioral/s3/check_bucket_intelligent_tiering_filtered_configs_replace_not_duplicate.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-intelligent-tiering-filtered-configs-replace-not-duplicate</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketIntelligentTieringFilteredConfigsReplaceNotDuplicate
{
    /// <summary>
    /// Two Intelligent-Tiering configs keep independent filters and tierings through List/Get, a
    /// re-Put of the same Id replaces that entry rather than duplicating it, and no object is created.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-intelligent-tiering-filtered-configs-replace-not-duplicate")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var unfiltered = Config("EntireBucket", "Enabled", (90, "ARCHIVE_ACCESS"));
        var filtered = Config("Filtered", "Disabled", (90, "ARCHIVE_ACCESS"), (180, "DEEP_ARCHIVE_ACCESS"));
        filtered.IntelligentTieringFilter = new IntelligentTieringFilter
        {
            IntelligentTieringFilterPredicate = new IntelligentTieringAndOperator(
            [
                new IntelligentTieringPrefixPredicate("logs/"),
                new IntelligentTieringTagPredicate(new Tag { Key = "env", Value = "prod" }),
            ]),
        };
        await Put(s3, bucket, unfiltered);
        await Put(s3, bucket, filtered);

        var byId = (await List(s3, bucket)).ToDictionary(c => c.IntelligentTieringId);
        Ensure(
            byId.Keys.ToHashSet().SetEquals(["EntireBucket", "Filtered"]),
            $"expected exactly the two configs just created, got {Py.Repr(byId.Keys.Order(StringComparer.Ordinal).ToList())}");
        Ensure(
            byId["EntireBucket"].IntelligentTieringFilter?.IntelligentTieringFilterPredicate is null,
            "EntireBucket was created with no Filter and must list with none");
        Ensure(
            FilterOf(byId["Filtered"]) == FilterOf(filtered),
            $"Filtered's And{{Prefix,Tags}} filter did not round-trip: {FilterOf(byId["Filtered"])}");
        Ensure(
            byId["Filtered"].Status?.Value == "Disabled" && Tierings(byId["Filtered"]).Count == 2,
            $"Filtered's Status/Tierings did not round-trip: {Py.Repr(byId["Filtered"].Status?.Value)} {Show(Tierings(byId["Filtered"]))}");
        Ensure(
            byId["EntireBucket"].Status?.Value == "Enabled" && Tierings(byId["EntireBucket"]).Count == 1,
            $"EntireBucket's Status/Tierings did not round-trip: {Py.Repr(byId["EntireBucket"].Status?.Value)} {Show(Tierings(byId["EntireBucket"]))}");

        var replacement = Config("EntireBucket", "Disabled", (180, "DEEP_ARCHIVE_ACCESS"));
        await Put(s3, bucket, replacement);
        var cfg = await Get(s3, bucket, "EntireBucket");
        Ensure(
            cfg.Status?.Value == "Disabled" && Tierings(cfg).SequenceEqual(Tierings(replacement)),
            $"GetBucketIntelligentTieringConfiguration after replace returned {Py.Repr(cfg.Status?.Value)} {Show(Tierings(cfg))}, expected the REPLACEMENT body");
        var listedAfter = await List(s3, bucket);
        var idsAfter = listedAfter.Select(c => c.IntelligentTieringId).ToList();
        Ensure(
            idsAfter.Order(StringComparer.Ordinal).SequenceEqual(["EntireBucket", "Filtered"]),
            $"replacing EntireBucket must not duplicate it; expected exactly 2 configs, got {Py.Repr(idsAfter)}");
        var byIdAfter = listedAfter.ToDictionary(c => c.IntelligentTieringId);
        Ensure(
            FilterOf(byIdAfter["Filtered"]) == FilterOf(filtered),
            $"replacing EntireBucket must leave the unrelated Filtered config untouched, but its Filter is now {FilterOf(byIdAfter["Filtered"])}");

        var objects = (await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket })).S3Objects ?? [];
        Ensure(
            objects.Count == 0,
            $"Intelligent-Tiering config calls must never create objects, but ListObjectsV2 found {Py.Repr(objects.Select(o => o.Key).ToList())}");

        await Delete(s3, bucket, "EntireBucket");
        await Delete(s3, bucket, "Filtered");
        var final = await List(s3, bucket);
        Ensure(final.Count == 0, $"expected no configs after deleting both, got {Py.Repr(final.Select(c => c.IntelligentTieringId).ToList())}");
    }

    /// <summary>A filter as the check compares it: its prefix and tags, under And or alone.</summary>
    private static string FilterOf(IntelligentTieringConfiguration configuration)
    {
        var predicate = configuration.IntelligentTieringFilter?.IntelligentTieringFilterPredicate;
        IEnumerable<IntelligentTieringFilterPredicate> operands = predicate is IntelligentTieringNAryOperator and ? and.Operands ?? [] : predicate is null ? [] : [predicate];
        var parts = operands.Select(p => p switch
        {
            IntelligentTieringPrefixPredicate prefix => $"Prefix={prefix.Prefix}",
            IntelligentTieringTagPredicate tag => $"Tag={tag.Tag?.Key}={tag.Tag?.Value}",
            _ => p.GetType().Name,
        });
        return (predicate is IntelligentTieringAndOperator ? "And" : string.Empty) + "{" + string.Join(", ", parts) + "}";
    }

    private static IntelligentTieringConfiguration Config(string id, string status, params (int Days, string Tier)[] tierings) => new()
    {
        IntelligentTieringId = id,
        Status = new IntelligentTieringStatus(status),
        Tierings = [.. tierings.Select(t => new Tiering { Days = t.Days, AccessTier = new IntelligentTieringAccessTier(t.Tier) })],
    };

    private static Task Put(AmazonS3Client s3, string bucket, IntelligentTieringConfiguration configuration) =>
        s3.PutBucketIntelligentTieringConfigurationAsync(new PutBucketIntelligentTieringConfigurationRequest
        {
            BucketName = bucket,
            IntelligentTieringId = configuration.IntelligentTieringId,
            IntelligentTieringConfiguration = configuration,
        });

    private static async Task<IntelligentTieringConfiguration> Get(AmazonS3Client s3, string bucket, string id) =>
        (await s3.GetBucketIntelligentTieringConfigurationAsync(new GetBucketIntelligentTieringConfigurationRequest { BucketName = bucket, IntelligentTieringId = id }))
            .IntelligentTieringConfiguration;

    private static async Task<List<IntelligentTieringConfiguration>> List(AmazonS3Client s3, string bucket) =>
        (await s3.ListBucketIntelligentTieringConfigurationsAsync(new ListBucketIntelligentTieringConfigurationsRequest { BucketName = bucket }))
            .IntelligentTieringConfigurationList ?? [];

    private static Task Delete(AmazonS3Client s3, string bucket, string id) =>
        s3.DeleteBucketIntelligentTieringConfigurationAsync(new DeleteBucketIntelligentTieringConfigurationRequest { BucketName = bucket, IntelligentTieringId = id });

    /// <summary>A configuration's tierings as the checks compare them: (Days, AccessTier) pairs, in order.</summary>
    private static List<(int Days, string Tier)> Tierings(IntelligentTieringConfiguration configuration) =>
        [.. (configuration.Tierings ?? []).Select(t => (t.Days ?? 0, t.AccessTier?.Value ?? string.Empty))];

    private static string Show(List<(int Days, string Tier)> tierings) =>
        "[" + string.Join(", ", tierings.Select(t => $"{{'Days': {t.Days}, 'AccessTier': {Py.Repr(t.Tier)}}}")) + "]";
}
