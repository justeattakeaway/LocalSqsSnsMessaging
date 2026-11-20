// Translated from behavioral/s3/check_bucket_tagging_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-tagging-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketTaggingRoundtrip
{
    /// <summary>
    /// PutBucketTagging then GetBucketTagging returns the same tags, byte-for-byte, at the
    /// documented bounds: 50 tags (the fixed per-bucket maximum), a 0-length value (the documented
    /// minimum), and non-ASCII/punctuation characters the tag charset explicitly allows.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-tagging-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        List<Tag> tagSet =
        [
            new() { Key = "project", Value = "sim2real" },
            new() { Key = "unicode-éè-中文", Value = "üñ-日本語" },
            new() { Key = "special.chars_test:val/1+2=3@x", Value = "" },
        ];
        tagSet.AddRange(Enumerable.Range(0, 47).Select(i => new Tag { Key = $"filler-key-{i}", Value = $"filler-value-{i}" }));
        Ensure(tagSet.Count == 50, $"test setup bug: built {tagSet.Count} tags, want 50");
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = bucket, TagSet = tagSet });
        var resp = await s3.GetBucketTaggingAsync(new GetBucketTaggingRequest { BucketName = bucket });
        var got = (resp.TagSet ?? []).ToDictionary(t => t.Key, t => t.Value ?? "", StringComparer.Ordinal);
        var want = tagSet.ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);
        Ensure(got.Count == 50, $"expected all 50 tags to roundtrip, got {got.Count}");
        Ensure(Py.Equal(got, want), $"tag mismatch: {Py.Repr(got)}");
    }
}
