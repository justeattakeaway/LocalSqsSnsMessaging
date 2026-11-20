// Translated from behavioral/s3/check_op_contract_put_bucket_tagging_invalid_tag_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-put-bucket-tagging-invalid-tag-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractPutBucketTaggingInvalidTagRejected
{
    // S3's refusal of a tag set with a duplicate key, as real S3 returns it.
    private static readonly (string?, string?) DuplicateTagKey = ("InvalidTag", "Cannot provide multiple Tags with the same key");

    /// <summary>
    /// PutBucketTagging replaces the tag set wholesale and rejects a duplicate key with InvalidTag.
    /// The rejected write leaves the existing tag set unchanged.
    /// </summary>
    [Test]
    [DisplayName("s3.op-contract-put-bucket-tagging-invalid-tag-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var bucket = await S3Fidelity.CreateBucket(ctx);
        var s3 = ctx.S3;

        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = bucket,
            TagSet = [new Tag { Key = "owner", Value = "teamA" }, new Tag { Key = "cost-center", Value = "123" }],
        });
        ctx.OnCleanup(() => s3.DeleteBucketTaggingAsync(new DeleteBucketTaggingRequest { BucketName = bucket }));
        var got = await Tags(s3, bucket);
        Ensure(Same(got, ("owner", "teamA"), ("cost-center", "123")), $"expected initial tag set, got {string.Join(", ", got)}");

        // PutBucketTagging fully replaces (not merges) the tag set
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = bucket, TagSet = [new Tag { Key = "owner", Value = "teamB" }] });
        var got2 = await Tags(s3, bucket);
        Ensure(Same(got2, ("owner", "teamB")), $"expected replace-not-merge semantics, got {string.Join(", ", got2)}");

        // A duplicate tag key is rejected.
        var code = await S3Fidelity.ErrorPair(() => s3.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = bucket,
            TagSet = [new Tag { Key = "dup", Value = "one" }, new Tag { Key = "dup", Value = "two" }],
        }));
        Ensure(code == DuplicateTagKey, $"expected InvalidTag for duplicate tag key, got {S3Fidelity.Repr(code)}");

        // the rejected write must not have mutated the previously-valid tag set
        var got3 = await Tags(s3, bucket);
        Ensure(Same(got3, ("owner", "teamB")), $"rejected PutBucketTagging must not partially apply, got {string.Join(", ", got3)}");
    }

    private static async Task<Dictionary<string, string>> Tags(IAmazonS3 s3, string bucket) =>
        ((await s3.GetBucketTaggingAsync(new GetBucketTaggingRequest { BucketName = bucket })).TagSet ?? [])
            .ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);

    private static bool Same(Dictionary<string, string> got, params (string Key, string Value)[] expected) =>
        got.Count == expected.Length && expected.All(e => got.TryGetValue(e.Key, out var v) && v == e.Value);
}
