// Translated from behavioral/s3/check_bucket_tagging_over_fifty_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-tagging-over-fifty-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketTaggingOverFiftyRejected
{
    /// <summary>S3's refusal of a bucket tag set past the fixed, non-adjustable 50-tag cap.</summary>
    private static readonly (string? Code, string? Message) TooManyBucketTags = ("BadRequest", "Bucket tag count cannot be greater than 50");

    /// <summary>
    /// PutBucketTagging accepts exactly 50 tags, its fixed non-adjustable per-bucket cap, and
    /// rejects a 51st tag without applying any part of the rejected write.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-tagging-over-fifty-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");

        var fifty = Enumerable.Range(0, 50).Select(i => new Tag { Key = $"k{i}", Value = $"v{i}" }).ToList();
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = bucket, TagSet = fifty });
        ctx.OnCleanup(() => s3.DeleteBucketTaggingAsync(new DeleteBucketTaggingRequest { BucketName = bucket }));
        var expected = AsDict(fifty);
        var atCap = AsDict((await s3.GetBucketTaggingAsync(new GetBucketTaggingRequest { BucketName = bucket })).TagSet);
        Ensure(Py.Equal(atCap, expected), $"50 tags, the documented cap, did not round-trip: {Py.Repr(atCap)}");

        List<Tag> fiftyOne = [.. fifty, new Tag { Key = "k50", Value = "v50" }];
        var got = await S3Fidelity.ErrorPair(() => s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = bucket, TagSet = fiftyOne }));
        Ensure(got == TooManyBucketTags, $"expected {S3Fidelity.Repr(TooManyBucketTags)} for 51 tags, got {S3Fidelity.Repr(got)}");

        var survived = AsDict((await s3.GetBucketTaggingAsync(new GetBucketTaggingRequest { BucketName = bucket })).TagSet);
        Ensure(Py.Equal(survived, expected), $"the rejected 51-tag write must not have mutated the existing tag set, got {Py.Repr(survived)}");
    }

    /// <summary><c>{t["Key"]: t["Value"] for t in tags}</c>.</summary>
    private static Dictionary<string, object?> AsDict(IEnumerable<Tag>? tags) =>
        (tags ?? []).ToDictionary(t => t.Key, t => (object?)t.Value, StringComparer.Ordinal);
}
