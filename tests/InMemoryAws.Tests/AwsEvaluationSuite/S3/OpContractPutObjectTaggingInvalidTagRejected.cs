// Translated from behavioral/s3/check_op_contract_put_object_tagging_invalid_tag_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-put-object-tagging-invalid-tag-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractPutObjectTaggingInvalidTagRejected
{
    // S3's refusal of a tag set with a duplicate key, as real S3 returns it.
    private static readonly (string?, string?) DuplicateTagKey = ("InvalidTag", "Cannot provide multiple Tags with the same key");

    /// <summary>PutObjectTagging replaces an object's tag set; rejects a duplicate tag key with InvalidTag.</summary>
    [Test]
    [DisplayName("s3.op-contract-put-object-tagging-invalid-tag-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var bucket = await S3Fidelity.CreateBucket(ctx);
        var s3 = ctx.S3;
        const string key = "obj-tags.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "payload" });
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, key));

        var putResp = await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = bucket,
            Key = key,
            Tagging = new Tagging { TagSet = [new Tag { Key = "phase", Value = "dev" }] },
        });
        var status = (int)putResp.HttpStatusCode;
        Ensure(putResp.VersionId is not null || putResp.HttpStatusCode == HttpStatusCode.OK, $"expected a successful PutObjectTagging, got HTTP {status}");

        var got = await Tags(s3, bucket, key);
        Ensure(got.Count == 1 && got.GetValueOrDefault("phase") == "dev", $"expected {{'phase': 'dev'}} after PutObjectTagging, got {string.Join(", ", got)}");

        // PutObjectTagging fully replaces (not merges) the object's tag set
        await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = bucket,
            Key = key,
            Tagging = new Tagging { TagSet = [new Tag { Key = "phase", Value = "prod" }] },
        });
        var got2 = await Tags(s3, bucket, key);
        Ensure(got2.Count == 1 && got2.GetValueOrDefault("phase") == "prod", $"expected replace-not-merge semantics, got {string.Join(", ", got2)}");

        // overly-permissive boundary: a duplicate tag key is not accepted
        var code = await S3Fidelity.ErrorPair(() => s3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = bucket,
            Key = key,
            Tagging = new Tagging { TagSet = [new Tag { Key = "dup", Value = "one" }, new Tag { Key = "dup", Value = "two" }] },
        }));
        Ensure(code == DuplicateTagKey, $"expected InvalidTag for duplicate tag key, got {S3Fidelity.Repr(code)}");

        // the rejected write must not have mutated the previously-valid tag set
        var got3 = await Tags(s3, bucket, key);
        Ensure(got3.Count == 1 && got3.GetValueOrDefault("phase") == "prod", $"rejected PutObjectTagging must not partially apply, got {string.Join(", ", got3)}");
    }

    private static async Task<Dictionary<string, string>> Tags(IAmazonS3 s3, string bucket, string key) =>
        ((await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key })).Tagging ?? [])
            .ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);
}
