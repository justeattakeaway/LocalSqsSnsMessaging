// Translated from behavioral/s3/check_object_tagging_exceeds_limit_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-tagging-exceeds-limit-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectTaggingExceedsLimitRejected
{
    /// <summary>
    /// PutObjectTagging with more than 10 tags must fail (overly-permissive boundary: S3 caps object tags at 10).
    /// </summary>
    [Test]
    [DisplayName("s3.object-tagging-exceeds-limit-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "overtagged.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(Encoding.UTF8.GetBytes("x")) });

        // positive control: a tag set exactly at the limit (10) must be accepted, so a later
        // rejection of 11 is provably about the count, not a target that rejects every request
        var atLimit = Enumerable.Range(0, 10).ToDictionary(i => $"k{i}", i => $"v{i}", StringComparer.Ordinal);
        await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = bucket,
            Key = key,
            Tagging = new Tagging { TagSet = [.. atLimit.Select(kv => new Tag { Key = kv.Key, Value = kv.Value })] },
        });
        var control = await Tags(s3, bucket, key);
        Ensure(Py.Equal(control, atLimit), $"expected the at-limit tag set to be accepted, got {Py.Repr(control)}");

        // break exactly one thing: one tag beyond the limit
        List<Tag> tooMany = [.. Enumerable.Range(0, 11).Select(i => new Tag { Key = $"k{i}", Value = $"v{i}" })];
        var err = await FidelityContext.CaptureError(() => s3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = bucket,
            Key = key,
            Tagging = new Tagging { TagSet = tooMany },
        })) ?? throw Fail("expected the call to be rejected, but it SUCCEEDED");
        Ensure(err.Code == "BadRequest", $"expected BadRequest for >10 tags, got {Py.Repr(err.Code)}");
        Ensure(err.Message == "Object tags cannot be greater than 10", $"unexpected message {Py.Repr(err.Message)}");

        // the rejected write must not have applied, merged or partially replaced the prior tag set
        var after = await Tags(s3, bucket, key);
        Ensure(Py.Equal(after, atLimit), $"expected the at-limit tag set unchanged after rejection, got {Py.Repr(after)}");
    }

    private static async Task<Dictionary<string, string>> Tags(IAmazonS3 s3, string bucket, string key) =>
        ((await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key })).Tagging ?? [])
            .ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);
}
