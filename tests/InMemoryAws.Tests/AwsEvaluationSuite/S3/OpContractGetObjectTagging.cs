// Translated from behavioral/s3/check_op_contract_get_object_tagging.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-getObjectTagging</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractGetObjectTagging
{
    // S3's refusal of a key that does not exist, as real S3 returns it.
    private static readonly (string?, string?) NoSuchKey = ("NoSuchKey", "The specified key does not exist.");

    /// <summary>GetObjectTagging returns the tag set put on an object; errors NoSuchKey for a missing key.</summary>
    [Test]
    [DisplayName("s3.op-contract-getObjectTagging")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var bucket = await S3Fidelity.CreateBucket(ctx);
        var s3 = ctx.S3;
        // GetObjectTagging only returns a VersionId when the bucket is versioned;
        // enable versioning so the VersionId contract can be asserted.
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });
        const string key = "tagged-object.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "hello" });
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, key));

        // a fresh object has an empty tag set, not an error
        var resp0 = await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key });
        Ensure((resp0.Tagging ?? []).Count == 0, $"expected empty TagSet, got {resp0.Tagging?.Count}");

        await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = bucket,
            Key = key,
            Tagging = new Tagging { TagSet = [new Tag { Key = "classification", Value = "internal" }] },
        });

        var resp1 = await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key });
        var got = (resp1.Tagging ?? []).ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);
        Ensure(got.Count == 1 && got.GetValueOrDefault("classification") == "internal", $"unexpected tag set: {string.Join(", ", got)}");
        Ensure(resp1.VersionId is not null, "expected a VersionId on a versioned bucket, got none");

        // distinct error boundary: nonexistent key
        var err = await FidelityContext.CaptureError(() => s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = "does-not-exist.txt" }))
            ?? throw Fail("expected a ClientError, but the call SUCCEEDED");
        Ensure((err.Code, err.Message) == NoSuchKey, $"expected NoSuchKey, got {err}");
    }
}
