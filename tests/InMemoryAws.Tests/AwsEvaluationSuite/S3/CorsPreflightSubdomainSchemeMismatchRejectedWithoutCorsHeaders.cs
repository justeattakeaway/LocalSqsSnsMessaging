// Translated from behavioral/s3/check_cors_preflight_subdomain_scheme_mismatch_rejected_without_cors_headers.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-preflight-subdomain-scheme-mismatch-rejected-without-cors-headers</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsPreflightSubdomainSchemeMismatchRejectedWithoutCorsHeaders
{
    private const string Check = "cors-preflight-subdomain-scheme-mismatch-rejected-without-cors-headers";
    private const string MatchingOrigin = "http://app.example.com";
    private const string MismatchedOrigin = "https://app.example.com";
    private const string Key = "spa/assets/data.json";
    private const string NotAllowedCode = "AccessForbidden";
    private const string NotAllowedMessage =
        "CORSResponse: This CORS request is not allowed. This is usually because the evalution of "
        + "Origin, request method / Access-Control-Request-Method or Access-Control-Request-Headers "
        + "are not whitelisted by the resource's CORS spec.";

    /// <summary>
    /// A subdomain wildcard rule for http:// echoes a matching http:// origin with 200, while the
    /// same subdomain over https:// is refused 403 AccessForbidden with no Access-Control-* headers.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-preflight-subdomain-scheme-mismatch-rejected-without-cors-headers")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = Key,
            InputStream = new MemoryStream("{\"ok\": true}"u8.ToArray()),
            ContentType = "application/json",
        });
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration { Rules = [new CORSRule { AllowedOrigins = ["http://*.example.com"], AllowedMethods = ["GET"] }] },
        });
        var url = S3Fidelity.ObjectUrl(ctx, bucket, Key);

        // An http:// origin under the wildcard is answered 200 with its origin echoed.
        var (status, headers, body) = await S3Fidelity.UntilPreflightStatus(
            ctx, url, new Dictionary<string, string> { ["Origin"] = MatchingOrigin, ["Access-Control-Request-Method"] = "GET" }, 200);
        Ensure(status == 200, $"the matching-scheme preflight must answer 200, got {status}: {S3Fidelity.Head(body, 300)}");
        Ensure(
            headers.GetValueOrDefault("access-control-allow-origin") == MatchingOrigin,
            $"the matching-scheme preflight's Allow-Origin is {Py.Repr(headers.GetValueOrDefault("access-control-allow-origin"))}");

        var requestHeaders = new Dictionary<string, string> { ["Origin"] = MismatchedOrigin, ["Access-Control-Request-Method"] = "GET" };
        (status, headers, body) = await S3Fidelity.Send(ctx, "OPTIONS", url, requestHeaders);
        Ensure(status == 403, $"the scheme-mismatched preflight must answer 403, got {status}: {S3Fidelity.Head(body, 300)}");
        var got = S3Fidelity.XmlError(body);
        var want = ((string?)NotAllowedCode, (string?)NotAllowedMessage, (string?)requestHeaders["Access-Control-Request-Method"], (string?)"OBJECT");
        Ensure(got == want, $"the scheme-mismatched preflight's error is {S3Fidelity.Repr(got)}, expected {S3Fidelity.Repr(want)}");
        var leaked = headers.Keys.Where(k => k.StartsWith("access-control-", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        Ensure(leaked.Count == 0, $"the scheme-mismatched refusal carries CORS headers {Py.Repr(leaked)}");
    }
}
