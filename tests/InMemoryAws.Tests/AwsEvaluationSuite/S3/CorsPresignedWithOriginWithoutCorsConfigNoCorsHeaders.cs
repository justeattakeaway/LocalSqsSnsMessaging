// Translated from behavioral/s3/check_cors_presigned_with_origin_without_cors_config_no_cors_headers.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-presigned-with-origin-without-cors-config-no-cors-headers</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsPresignedWithOriginWithoutCorsConfigNoCorsHeaders
{
    /// <summary>
    /// A presigned PUT and GET sent with a browser Origin to a bucket with no CORS configuration are served
    /// as ordinary requests with no Access-Control-* header or Vary; the same GET to a sibling bucket whose
    /// CORS rule allows the origin carries Allow-Origin.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-presigned-with-origin-without-cors-config-no-cors-headers")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;

        // Positive control: an identical twin bucket WITH a CORS rule for the same origin. Without it,
        // "no CORS headers below" cannot be told from a target that never emits CORS headers at all.
        var twin = await S3Fidelity.NewBucket(ctx);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = twin, Key = Key, InputStream = new MemoryStream(Body), ContentType = "application/json" });
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = twin,
            Configuration = new CORSConfiguration { Rules = [new CORSRule { AllowedOrigins = [Origin], AllowedMethods = ["GET"] }] },
        });
        await UntilStatus(ctx, S3Fidelity.ObjectUrl(ctx, twin, Key), new Dictionary<string, string> { ["Origin"] = Origin, ["Access-Control-Request-Method"] = "GET" }, 200);
        var twinUrl = S3Fidelity.Presign(ctx, "GET", twin, Key, 300, pathStyle: true);
        var (status, headers, body) = await S3Fidelity.Send(ctx, "GET", twinUrl, new Dictionary<string, string> { ["Origin"] = Origin });
        Ensure(status == 200, $"the configured twin's presigned GET must answer 200, got {status}: {S3Fidelity.Head(body, 300)}");
        Ensure(
            headers.GetValueOrDefault("access-control-allow-origin") == Origin,
            $"the configured twin's presigned GET has Allow-Origin {Py.Repr(headers.GetValueOrDefault("access-control-allow-origin"))}");
        Ensure(
            (headers.GetValueOrDefault("vary") ?? string.Empty).Split(',').Select(t => t.Trim().ToLowerInvariant()).Contains("origin"),
            $"the configured twin's presigned GET has Vary {Py.Repr(headers.GetValueOrDefault("vary"))}");

        var bucket = await S3Fidelity.NewBucket(ctx);
        var putUrl = S3Fidelity.Presign(ctx, "PUT", bucket, Key, 300, pathStyle: true);
        var getUrl = S3Fidelity.Presign(ctx, "GET", bucket, Key, 300, pathStyle: true);
        var md5 = Convert.ToHexStringLower(MD5.HashData(Body));
        (status, headers, body) = await S3Fidelity.Send(ctx, "PUT", putUrl, new Dictionary<string, string> { ["Origin"] = Origin, ["Content-Type"] = "application/json" }, Body);
        Ensure(status == 200, $"the presigned PUT with Origin must answer 200, got {status}: {S3Fidelity.Head(body, 300)}");
        Ensure(headers.GetValueOrDefault("etag") == $"\"{md5}\"", $"the presigned PUT's ETag is {Py.Repr(headers.GetValueOrDefault("etag"))}");
        AssertNoCors(headers, "PUT");

        (status, headers, body) = await S3Fidelity.Send(ctx, "GET", getUrl, new Dictionary<string, string> { ["Origin"] = Origin });
        Ensure(status == 200, $"the presigned GET with Origin must answer 200, got {status}: {S3Fidelity.Head(body, 300)}");
        Ensure(body.SequenceEqual(Body), $"the presigned GET served {S3Fidelity.Head(body, 300)}, expected {S3Fidelity.Head(Body)}");
        AssertNoCors(headers, "GET");

        var stored = await s3.GetObjectMetadataAsync(bucket, Key);
        Ensure(
            stored.ContentLength == Body.Length && stored.ETag == $"\"{md5}\"",
            $"HeadObject reads (ContentLength, ETag) ({stored.ContentLength}, {Py.Repr(stored.ETag)})");
    }

    private const string Origin = "https://app.example.com";
    private const string Key = "uploads/report.json";
    private static readonly byte[] Body = "{\"report\": \"quarterly\", \"rows\": 3}"u8.ToArray();

    /// <summary>Without a CORS configuration the response names no CORS header and no Vary.</summary>
    private static void AssertNoCors(Dictionary<string, string> headers, string method)
    {
        var got = headers.Keys.Where(k => k.StartsWith("access-control-", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        Ensure(got.Count == 0, $"the presigned {method} without CORS configuration carries {Py.Repr(got)}");
        Ensure(!headers.ContainsKey("vary"), $"the presigned {method}'s Vary is {Py.Repr(headers.GetValueOrDefault("vary"))}");
    }

    /// <summary>
    /// <c>_until_status(ctx, url, headers, want)</c>: the answer of a preflight to <paramref name="url"/>
    /// once it is <paramref name="want"/> (a CORS configuration change propagates).
    /// </summary>
    private static async Task UntilStatus(FidelityContext ctx, string url, Dictionary<string, string> headers, int want, double budgetSeconds = 30.0)
    {
        budgetSeconds = Math.Min(budgetSeconds, ctx.TimeLeftSeconds / 3);
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var (status, _, body) = await S3Fidelity.Send(ctx, "OPTIONS", url, headers);
            if (status == want)
            {
                return;
            }

            if (clock.Elapsed.TotalSeconds > budgetSeconds)
            {
                throw Fail($"the preflight with {Py.Repr(headers)} answered {status}, not {want}, for {budgetSeconds:F0}s: {S3Fidelity.Head(body, 300)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }
}
