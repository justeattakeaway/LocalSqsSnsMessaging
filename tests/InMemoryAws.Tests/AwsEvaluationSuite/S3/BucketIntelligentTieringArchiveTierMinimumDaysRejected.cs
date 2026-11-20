// Translated from behavioral/s3/check_bucket_intelligent_tiering_archive_tier_minimum_days_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-intelligent-tiering-archive-tier-minimum-days-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketIntelligentTieringArchiveTierMinimumDaysRejected
{
    /// <summary>
    /// ARCHIVE_ACCESS below 90 days and DEEP_ARCHIVE_ACCESS below 180 are rejected InvalidAccessTier
    /// and never stored, a floor tighter than the model's Days range; 90-day ARCHIVE is accepted.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-intelligent-tiering-archive-tier-minimum-days-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var bad = Config("TooSoon", "Enabled", (30, "ARCHIVE_ACCESS"));
        var (code, message) = await Rejection(() => Put(s3, bucket, bad));
        Ensure(code == "InvalidAccessTier", $"expected InvalidAccessTier for an ARCHIVE_ACCESS tiering of 30 days, got {Py.Repr(code)}");
        Ensure(
            message == "Days specified in ARCHIVE_ACCESS tier should not be less than 90.",
            $"InvalidAccessTier for ARCHIVE_ACCESS at 30 days must carry S3's own message; got {Py.Repr(message)}");
        // The .NET SDK answers GetBucketIntelligentTieringConfiguration's 404 as an empty success; the
        // refusal is read off the wire.
        var missingError = await S3Fidelity.RawError(ctx, "GET", $"{bucket}?intelligent-tiering&id=TooSoon", "the call succeeded where real AWS rejects it");
        var (missing, missingMessage) = (missingError.Code, missingError.Message);
        Ensure(
            missing == "NoSuchConfiguration",
            $"the rejected config must not exist; GetBucketIntelligentTieringConfiguration returned {Py.Repr(missing)} instead of NoSuchConfiguration");
        Ensure(
            missingMessage == "The specified configuration does not exist.",
            $"NoSuchConfiguration must carry S3's own message; got {Py.Repr(missingMessage)}");
        var listed = await List(s3, bucket);
        Ensure(listed.Count == 0, $"the rejected config must not be listed, got {Py.Repr(listed.Select(c => c.IntelligentTieringId).ToList())}");

        var good = Config("AtFloor", "Enabled", (90, "ARCHIVE_ACCESS"));
        await Put(s3, bucket, good);
        ctx.OnCleanup(() => Delete(s3, bucket, "AtFloor"));
        var cfg = await Get(s3, bucket, "AtFloor");
        Ensure(Tierings(cfg).SequenceEqual(Tierings(good)), $"the 90-day control config did not round-trip: {Show(Tierings(cfg))}");

        var badDeep = Config("DeepTooSoon", "Enabled", (90, "DEEP_ARCHIVE_ACCESS"));
        var (deepCode, deepMessage) = await Rejection(() => Put(s3, bucket, badDeep));
        Ensure(deepCode == "InvalidAccessTier", $"expected InvalidAccessTier for a DEEP_ARCHIVE_ACCESS tiering of 90 days, got {Py.Repr(deepCode)}");
        Ensure(
            deepMessage == "Days specified in DEEP_ARCHIVE_ACCESS tier should not be less than 180.",
            $"InvalidAccessTier for DEEP_ARCHIVE_ACCESS at 90 days must carry S3's own message; got {Py.Repr(deepMessage)}");
    }

    /// <summary>_rejection(fn): (code, message) of the AWS error fn() raises; fails when the call SUCCEEDS.</summary>
    private static Task<(string? Code, string? Message)> Rejection(Func<Task> fn) =>
        S3Fidelity.ClientError(fn, "the call succeeded where real AWS rejects it");

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
