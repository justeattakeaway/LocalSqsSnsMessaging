// Translated from behavioral/s3/check_bucket_intelligent_tiering_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-intelligent-tiering-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketIntelligentTieringRoundtrip
{
    /// <summary>
    /// Intelligent-Tiering configurations round-trip their tiering rules and are removable.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-intelligent-tiering-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string tid = "archive-after-90d";
        await Put(s3, bucket, Config(tid, "Enabled", (90, "ARCHIVE_ACCESS")));
        var got = await Get(s3, bucket, tid);
        Ensure(got.Status?.Value == "Enabled", $"status not preserved: {Py.Repr(got.Status?.Value)}");
        Ensure(Tierings(got).SequenceEqual([(90, "ARCHIVE_ACCESS")]), $"tierings not preserved: {Show(Tierings(got))}");
        var listed = await List(s3, bucket);
        Ensure(listed.Select(c => c.IntelligentTieringId).SequenceEqual([tid]), $"expected exactly ['{tid}'] listed, got {Py.Repr(listed.Select(c => c.IntelligentTieringId).ToList())}");
        await Delete(s3, bucket, tid);
        // The .NET SDK answers GetBucketIntelligentTieringConfiguration's 404 as an empty success; the
        // refusal is read off the wire.
        var error = await S3Fidelity.RawError(ctx, "GET", $"{bucket}?intelligent-tiering&id={tid}");
        var (code, message) = (error.Code, error.Message);
        Ensure(code == "NoSuchConfiguration", $"expected NoSuchConfiguration, got {Py.Repr(code)}");
        const string expected = "The specified configuration does not exist.";
        Ensure(message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(message)}");
        var listedAfter = await List(s3, bucket);
        Ensure(listedAfter.Count == 0, $"expected no configurations listed after delete, got {Py.Repr(listedAfter.Select(c => c.IntelligentTieringId).ToList())}");
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
