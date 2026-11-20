// Translated from behavioral/s3/check_content_type_preserved.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.content-type-preserved</summary>
[Category("AwsEvaluationSuite")]
public sealed class ContentTypePreserved
{
    /// <summary>ContentType specified at upload is preserved on retrieval.</summary>
    [Test]
    [DisplayName("s3.content-type-preserved")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = "app.json",
            InputStream = new MemoryStream(Encoding.UTF8.GetBytes("{\"a\":1}")),
            ContentType = "application/json",
        });
        using var resp = await s3.GetObjectAsync(bucket, "app.json");
        var contentType = resp.Headers.ContentType;
        Ensure(
            contentType == "application/json",
            $"ContentType not preserved: expected 'application/json', got {Py.Repr(contentType)}");
    }
}
