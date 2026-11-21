// Translated from behavioral/s3/check_cors_preflight_without_a_request_method_header_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-preflight-without-a-request-method-header-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsPreflightWithoutARequestMethodHeaderRejected
{
    private const string Check = "cors-preflight-without-a-request-method-header-rejected";
    private const string Site = "https://shop.example.com";
    private const string ObjectKey = "catalogue/page-1.json";
    private static readonly byte[] ObjectBody = "{\"items\": []}"u8.ToArray();

    /// <summary>
    /// A preflight that carries Origin but omits Access-Control-Request-Method is refused with
    /// its exact code and message, while the same preflight naming the allowed method answers 200.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-preflight-without-a-request-method-header-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check, "sim2real-behav-cors-");
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = ObjectKey,
            InputStream = new MemoryStream(ObjectBody),
            ContentType = "application/json",
        });
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration { Rules = [new CORSRule { AllowedOrigins = [Site], AllowedMethods = ["GET"], AllowedHeaders = ["content-type"] }] },
        });
        var browserUrl = S3Fidelity.ObjectUrl(ctx, bucket, ObjectKey);
        var complete = new Dictionary<string, string> { ["Origin"] = Site, ["Access-Control-Request-Method"] = "GET" };

        // The grant has to be in effect before the omission can be blamed for anything.
        var granted = await S3Fidelity.AwaitAnswer(
            ctx,
            "OPTIONS",
            browserUrl,
            complete,
            a => a.Status == 200,
            (a, budget) => $"the preflight was still answering {a.Status} after {budget:F0}s, never 200: {S3Fidelity.Head(a.Body, 300)}");
        Ensure(
            granted.Headers.GetValueOrDefault("access-control-allow-methods") == "GET",
            $"the complete preflight answered Access-Control-Allow-Methods {Py.Repr(granted.Headers.GetValueOrDefault("access-control-allow-methods"))}, expected 'GET'");

        var headless = await S3Fidelity.Send(ctx, "OPTIONS", browserUrl, new Dictionary<string, string> { ["Origin"] = Site });
        Ensure(headless.Status == 403, $"the preflight without a requested method answered {headless.Status}, expected 403: {S3Fidelity.Head(headless.Body, 300)}");
        Ensure(S3Fidelity.XmlFields(headless.Body) is not null, $"the refusal is not an S3 XML document: {S3Fidelity.Head(headless.Body, 300)}");
        var refusal = S3Fidelity.XmlCodeMessage(headless.Body);

        // S3's refusal text verbatim, including its own wording; the check grades the exact message.
        (string?, string?) expected = (
            "AccessForbidden",
            "CORSResponse: This CORS request is not allowed. This is usually because the evalution of "
            + "Origin, request method / Access-Control-Request-Method or Access-Control-Request-Headers "
            + "are not whitelisted by the resource's CORS spec.");
        Ensure(refusal == expected, $"the preflight without a requested method was refused as {S3Fidelity.Repr(refusal)}, expected {S3Fidelity.Repr(expected)}");
        var grantedHeaders = headless.Headers.Keys.Where(k => k.StartsWith("access-control-", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        Ensure(grantedHeaders.Count == 0, $"the refused preflight still carried CORS headers {Py.Repr(grantedHeaders)}");

        // The rule is untouched, so the method-bearing preflight must still be granted.
        var again = await S3Fidelity.Send(ctx, "OPTIONS", browserUrl, complete);
        Ensure(again.Status == 200, $"the complete preflight answered {again.Status} after the refusal, expected 200: {S3Fidelity.Head(again.Body, 300)}");
        Ensure(
            again.Headers.GetValueOrDefault("access-control-allow-origin") == Site,
            $"the complete preflight answered Access-Control-Allow-Origin {Py.Repr(again.Headers.GetValueOrDefault("access-control-allow-origin"))}, expected {Py.Repr(Site)}");
    }
}
