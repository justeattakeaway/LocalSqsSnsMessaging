// Translated from behavioral/s3/check_op_contract_get_bucket_tagging.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-getBucketTagging</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractGetBucketTagging
{
    // S3's refusal of a bucket with no tag set, as real S3 returns it.
    private static readonly (string?, string?) NoSuchTagSet = ("NoSuchTagSet", "The TagSet does not exist");

    /// <summary>GetBucketTagging returns the exact tag set that was put; errors NoSuchTagSet when none set.</summary>
    [Test]
    [DisplayName("s3.op-contract-getBucketTagging")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        // This check's _create_bucket (its bucket prefix is sim2real-behav-) tags nothing.
        var bucket = await S3Fidelity.CreateBucket(ctx);
        var s3 = ctx.S3;

        // success path: no tags yet -> NoSuchTagSet
        var code = await Error(() => s3.GetBucketTaggingAsync(new GetBucketTaggingRequest { BucketName = bucket }));
        Ensure(code == NoSuchTagSet, $"expected NoSuchTagSet before any tags set, got {code}");

        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = bucket,
            TagSet = [new Tag { Key = "env", Value = "prod" }, new Tag { Key = "team", Value = "data" }],
        });
        var resp = await s3.GetBucketTaggingAsync(new GetBucketTaggingRequest { BucketName = bucket });
        var got = (resp.TagSet ?? []).ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);
        Ensure(
            Py.Equal(got, new Dictionary<string, string> { ["env"] = "prod", ["team"] = "data" }),
            $"unexpected tag set: {Py.Repr(got)}");

        // distinct error boundary: after DeleteBucketTagging, GetBucketTagging fails again
        await s3.DeleteBucketTaggingAsync(new DeleteBucketTaggingRequest { BucketName = bucket });
        var code2 = await Error(() => s3.GetBucketTaggingAsync(new GetBucketTaggingRequest { BucketName = bucket }));
        Ensure(code2 == NoSuchTagSet, $"expected NoSuchTagSet after delete, got {code2}");
    }

    /// <summary>Return the ClientError (Code, Message) of fn(); raise AssertionError when fn() succeeds.</summary>
    private static async Task<(string?, string?)> Error(Func<Task> fn)
    {
        var err = await FidelityContext.CaptureError(fn) ?? throw Fail("expected a ClientError, but the call SUCCEEDED");
        return (err.Code, err.Message);
    }
}
