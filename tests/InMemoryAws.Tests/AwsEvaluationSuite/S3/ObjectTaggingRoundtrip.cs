// Translated from behavioral/s3/check_object_tagging_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-tagging-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectTaggingRoundtrip
{
    /// <summary>
    /// PutObjectTagging then GetObjectTagging returns the same tags, and a second
    /// PutObjectTagging call REPLACES the tag set rather than merging into it.
    /// </summary>
    [Test]
    [DisplayName("s3.object-tagging-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "tagged.txt", InputStream = new MemoryStream(Encoding.UTF8.GetBytes("data")) });
        await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = bucket,
            Key = "tagged.txt",
            Tagging = new Tagging { TagSet = [new Tag { Key = "env", Value = "test" }, new Tag { Key = "team", Value = "sim2real" }] },
        });
        var got = await Tags(s3, bucket, "tagged.txt");
        Ensure(Py.Equal(got, new Dictionary<string, string> { ["env"] = "test", ["team"] = "sim2real" }), $"tag mismatch: {Py.Repr(got)}");

        await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = bucket,
            Key = "tagged.txt",
            Tagging = new Tagging { TagSet = [new Tag { Key = "stage", Value = "prod" }] },
        });
        var got2 = await Tags(s3, bucket, "tagged.txt");
        Ensure(Py.Equal(got2, new Dictionary<string, string> { ["stage"] = "prod" }), $"expected PutObjectTagging to replace, not merge: {Py.Repr(got2)}");
    }

    private static async Task<Dictionary<string, string>> Tags(IAmazonS3 s3, string bucket, string key) =>
        ((await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key })).Tagging ?? [])
            .ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);
}
