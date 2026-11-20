// Translated from behavioral/s3/check_cors_presigned_put_and_get_with_origin_carry_allow_origin.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-presigned-put-and-get-with-origin-carry-allow-origin</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsPresignedPutAndGetWithOriginCarryAllowOrigin
{
    /// <summary>
    /// A presigned PUT and GET sent with a browser Origin a CORS rule allows succeed, store and serve the
    /// bytes, and answer with that rule's Access-Control-* headers and a Vary naming Origin and both
    /// Access-Control-Request-* headers.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-presigned-put-and-get-with-origin-carry-allow-origin")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration
            {
                Rules =
                [
                    new CORSRule
                    {
                        AllowedOrigins = [Origin],
                        AllowedMethods = ["GET", "PUT"],
                        AllowedHeaders = ["*"],
                        ExposeHeaders = ["ETag"],
                        MaxAgeSeconds = 600,
                    },
                ],
            },
        });
        var putUrl = S3Fidelity.Presign(ctx, "PUT", bucket, Key, 300, pathStyle: true);
        var getUrl = S3Fidelity.Presign(ctx, "GET", bucket, Key, 300, pathStyle: true);
        var md5 = Convert.ToHexStringLower(MD5.HashData(Body));

        // A new CORS configuration propagates: wait until the rule answers a PUT preflight.
        await UntilStatus(ctx, S3Fidelity.ObjectUrl(ctx, bucket, Key), new Dictionary<string, string> { ["Origin"] = Origin, ["Access-Control-Request-Method"] = "PUT" }, 200);
        var (status, headers, body) = await S3Fidelity.Send(ctx, "PUT", putUrl, new Dictionary<string, string> { ["Origin"] = Origin, ["Content-Type"] = "application/json" }, Body);
        Ensure(status == 200, $"the presigned PUT with Origin must answer 200, got {status}: {S3Fidelity.Head(body, 300)}");
        Ensure(headers.GetValueOrDefault("etag") == $"\"{md5}\"", $"the presigned PUT's ETag is {Py.Repr(headers.GetValueOrDefault("etag"))}");
        AssertCors(headers, "PUT");

        (status, headers, body) = await S3Fidelity.Send(ctx, "GET", getUrl, new Dictionary<string, string> { ["Origin"] = Origin });
        Ensure(status == 200, $"the presigned GET with Origin must answer 200, got {status}: {S3Fidelity.Head(body, 300)}");
        Ensure(body.SequenceEqual(Body), $"the presigned GET served {S3Fidelity.Head(body, 300)}, expected {S3Fidelity.Head(Body)}");
        Ensure(
            headers.GetValueOrDefault("content-type") == "application/json",
            $"the presigned GET's Content-Type is {Py.Repr(headers.GetValueOrDefault("content-type"))}");
        AssertCors(headers, "GET");
    }

    private const string Origin = "https://app.example.com";
    private const string Key = "uploads/report.json";
    private static readonly byte[] Body = "{\"report\": \"quarterly\", \"rows\": 3}"u8.ToArray();

    /// <summary>The actual response carries the matching rule's CORS headers and varies on Origin.</summary>
    private static void AssertCors(Dictionary<string, string> headers, string method)
    {
        var got = headers.Where(h => h.Key.StartsWith("access-control-", StringComparison.Ordinal)).OrderBy(h => h.Key, StringComparer.Ordinal).ToList();
        var want = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["access-control-allow-origin"] = Origin,
            ["access-control-allow-methods"] = "GET, PUT",
            ["access-control-expose-headers"] = "ETag",
            ["access-control-max-age"] = "600",
            ["access-control-allow-credentials"] = "true",
        };
        Ensure(
            got.Count == want.Count && got.All(h => want.GetValueOrDefault(h.Key) == h.Value),
            $"the presigned {method}'s CORS headers are {Py.Repr(got.ToDictionary(h => h.Key, h => h.Value))}, expected {Py.Repr(want.ToDictionary(h => h.Key, h => h.Value))}");
        var vary = Tokens(headers.GetValueOrDefault("vary"));
        Ensure(
            vary.SetEquals(["origin", "access-control-request-headers", "access-control-request-method"]),
            $"the presigned {method}'s Vary is {Py.Repr(headers.GetValueOrDefault("vary"))}");
    }

    /// <summary>The lower-cased, comma-separated tokens of a header value.</summary>
    private static HashSet<string> Tokens(string? value) =>
        [.. (value ?? string.Empty).Split(',').Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0)];

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
