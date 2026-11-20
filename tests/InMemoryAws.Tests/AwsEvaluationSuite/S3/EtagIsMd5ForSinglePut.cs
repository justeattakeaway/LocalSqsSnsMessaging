// Translated from behavioral/s3/check_etag_is_md5_for_single_put.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.etag-is-md5-for-single-put</summary>
[Category("AwsEvaluationSuite")]
public sealed class EtagIsMd5ForSinglePut
{
    /// <summary>
    /// ETag for a non-multipart PUT is the hex MD5 of the body (quoted), and that holds on an
    /// independent read back, not just the PutObject response.
    /// </summary>
    [Test]
    [DisplayName("s3.etag-is-md5-for-single-put")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var body = Encoding.ASCII.GetBytes("etag-test-body-sim2real");
        const string key = "etag-test";
        var resp = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body) });
        var expectedMd5 = S3Fidelity.Md5Hex(body);
        // ETag is returned with surrounding quotes
        var putEtag = resp.ETag.Trim('"');
        Ensure(putEtag == expectedMd5, $"PutObject ETag {putEtag} != expected MD5 {expectedMd5}");

        var head = await s3.GetObjectMetadataAsync(bucket, key);
        var headEtag = head.ETag.Trim('"');
        Ensure(headEtag == expectedMd5, $"HeadObject ETag {headEtag} != expected MD5 {expectedMd5} on independent read");
    }
}
