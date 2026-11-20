// Translated from behavioral/s3/check_object_tagging_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-tagging-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectTaggingDelete
{
    /// <summary>DeleteObjectTagging removes all tags.</summary>
    [Test]
    [DisplayName("s3.object-tagging-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "tag-del.txt", InputStream = new MemoryStream(Encoding.UTF8.GetBytes("data")) });
        await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = bucket,
            Key = "tag-del.txt",
            Tagging = new Tagging { TagSet = [new Tag { Key = "remove-me", Value = "yes" }] },
        });
        var before = (await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = "tag-del.txt" })).Tagging ?? [];
        Ensure(
            before.Count == 1 && before[0].Key == "remove-me" && before[0].Value == "yes",
            $"expected the tag to be set before delete, got {Py.Repr(before.Select(t => $"{t.Key}={t.Value}").ToList())}");

        await s3.DeleteObjectTaggingAsync(new DeleteObjectTaggingRequest { BucketName = bucket, Key = "tag-del.txt" });
        var resp = (await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = "tag-del.txt" })).Tagging ?? [];
        Ensure(resp.Count == 0, $"expected empty TagSet, got {Py.Repr(resp.Select(t => $"{t.Key}={t.Value}").ToList())}");
    }
}
