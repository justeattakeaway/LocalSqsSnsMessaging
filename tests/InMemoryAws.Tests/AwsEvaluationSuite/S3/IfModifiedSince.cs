// Translated from behavioral/s3/check_if_modified_since.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Net;
using System.Text;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.if-modified-since</summary>
[Category("AwsEvaluationSuite")]
public sealed class IfModifiedSince
{
    /// <summary>
    /// GetObject honors If-Modified-Since in both directions: a date before the object's
    /// creation returns the object, and the object's own Last-Modified value — under which the
    /// object has, by definition, not been modified since — returns 304 Not Modified.
    /// </summary>
    [Test]
    [DisplayName("s3.if-modified-since")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "mod.txt", InputStream = new MemoryStream(Encoding.UTF8.GetBytes("modified-since-test")) });

        // A date in the past — object was modified after this, so it SHOULD be returned
        var past = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime lastModified;
        using (var resp = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "mod.txt", ModifiedSinceDate = past }))
        {
            var status = (int)resp.HttpStatusCode;
            Ensure(status == 200, $"expected 200 for If-Modified-Since={past.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)}, got {status}");
            var body = Encoding.UTF8.GetString(await S3Fidelity.ReadBody(resp));
            Ensure(body == "modified-since-test", $"expected 'modified-since-test', got {Py.Repr(body)}");
            lastModified = resp.LastModified!.Value.ToUniversalTime();
        }

        // If-Modified-Since set to the object's own Last-Modified — it has not been modified since
        // itself, so this direction MUST 304. Without this leg, a target that ignores the header
        // entirely and always returns the object would pass the check above unchanged.
        var exc = await FidelityContext.CaptureError(async () =>
        {
            using var _ = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = "mod.txt", ModifiedSinceDate = lastModified });
        }) ?? throw Fail(
            $"expected 304 for If-Modified-Since={lastModified.ToString("o", CultureInfo.InvariantCulture)} "
            + "(the object's own Last-Modified), but the request succeeded");
        var notModifiedStatus = (int)exc.StatusCode;
        Ensure(exc.StatusCode == HttpStatusCode.NotModified, $"expected 304, got status {notModifiedStatus}");
    }
}
