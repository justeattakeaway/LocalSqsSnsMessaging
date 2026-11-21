// Translated from behavioral/s3/check_cors_preflight_for_presigned_put_with_unlisted_request_header_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-preflight-for-presigned-put-with-unlisted-request-header-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsPreflightForPresignedPutWithUnlistedRequestHeaderRejected
{
    private const string Check = "cors-preflight-for-presigned-put-with-unlisted-request-header-rejected";
    private const string Origin = "https://app.example.com";
    private const string Key = "uploads/form.json";
    private const string Refused =
        "CORSResponse: This CORS request is not allowed. This is usually because the evalution of "
        + "Origin, request method / Access-Control-Request-Method or Access-Control-Request-Headers "
        + "are not whitelisted by the resource's CORS spec.";

    /// <summary>
    /// A PUT preflight asking for a request header the rule's AllowedHeaders omits is refused 403
    /// AccessForbidden with its exact message; asking for a listed header returns Allow-Headers.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-preflight-for-presigned-put-with-unlisted-request-header-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check);
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration { Rules = [new CORSRule { AllowedOrigins = [Origin], AllowedMethods = ["PUT"], AllowedHeaders = ["content-type"] }] },
        });
        var url = S3Fidelity.ObjectUrl(ctx, bucket, Key);

        // Positive control: the listed header is granted and echoed.
        var (_, headers, _) = await S3Fidelity.UntilPreflightStatus(
            ctx,
            url,
            new Dictionary<string, string>
            {
                ["Origin"] = Origin,
                ["Access-Control-Request-Method"] = "PUT",
                ["Access-Control-Request-Headers"] = "content-type",
            },
            200);
        var want = new Dictionary<string, string>
        {
            ["access-control-allow-origin"] = Origin,
            ["access-control-allow-methods"] = "PUT",
            ["access-control-allow-headers"] = "content-type",
            ["access-control-allow-credentials"] = "true",
        };
        Ensure(S3Fidelity.SameHeaders(S3Fidelity.Cors(headers), want), $"the granted preflight's CORS headers are {Py.Repr(S3Fidelity.Cors(headers))}");

        var (status, refusedHeaders, body) = await S3Fidelity.Send(
            ctx,
            "OPTIONS",
            url,
            new Dictionary<string, string>
            {
                ["Origin"] = Origin,
                ["Access-Control-Request-Method"] = "PUT",
                ["Access-Control-Request-Headers"] = "content-type,x-amz-meta-note",
            });
        Ensure(status == 403, $"the preflight with an unlisted header answered {status}: {S3Fidelity.Head(body, 300)}");
        var got = S3Fidelity.XmlError(body);
        Ensure(
            got == ("AccessForbidden", Refused, "PUT", "OBJECT"),
            $"the refused preflight's (Code, Message, Method, ResourceType) is {S3Fidelity.Repr(got)}");
        Ensure(S3Fidelity.Cors(refusedHeaders).Count == 0, $"the refused preflight carries CORS headers {Py.Repr(S3Fidelity.Cors(refusedHeaders))}");

        // The refusal is the preflight's alone: the presigned PUT itself still stores the object.
        var putUrl = S3Fidelity.Presign(ctx, "PUT", bucket, Key, 300, pathStyle: true);
        (status, _, body) = await S3Fidelity.Send(ctx, "PUT", putUrl, new Dictionary<string, string> { ["Origin"] = Origin }, "{}"u8.ToArray());
        Ensure(status == 200, $"the presigned PUT answered {status}: {S3Fidelity.Head(body, 300)}");
        var gotBody = await S3Fidelity.GetBytes(s3, bucket, Key);
        Ensure(gotBody.SequenceEqual("{}"u8.ToArray()), $"GetObject reads {S3Fidelity.Head(gotBody)} after the presigned PUT");
    }
}
