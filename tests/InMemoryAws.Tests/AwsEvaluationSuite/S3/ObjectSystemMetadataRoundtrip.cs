// Translated from behavioral/s3/check_object_system_metadata_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-system-metadata-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectSystemMetadataRoundtrip
{
    /// <summary>
    /// HTTP system metadata headers set at PutObject are persisted and replayed verbatim on
    /// GetObject/HeadObject.
    /// </summary>
    [Test]
    [DisplayName("s3.object-system-metadata-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-s3fid-");
        const string key = "sysmeta.txt";
        var put = new PutObjectRequest { BucketName = name, Key = key, ContentBody = "x", ContentType = "text/csv" };
        put.Headers.CacheControl = "max-age=42, private";
        put.Headers.ContentDisposition = "attachment; filename=\"report.csv\"";
        put.Headers["Content-Language"] = "en-GB";
        await s3.PutObjectAsync(put);

        HeadersCollection get;
        using (var got = await s3.GetObjectAsync(name, key))
        {
            get = got.Headers;
        }

        foreach (var headers in new[] { get, (await s3.GetObjectMetadataAsync(name, key)).Headers })
        {
            Ensure(headers.CacheControl == "max-age=42, private", $"CacheControl not preserved: {Py.Repr(headers.CacheControl)}");
            Ensure(headers.ContentDisposition == "attachment; filename=\"report.csv\"", $"ContentDisposition not preserved: {Py.Repr(headers.ContentDisposition)}");
            Ensure(headers["Content-Language"] == "en-GB", $"ContentLanguage not preserved: {Py.Repr(headers["Content-Language"])}");
            Ensure(headers.ContentType == "text/csv", $"ContentType not preserved: {Py.Repr(headers.ContentType)}");
        }
    }
}
