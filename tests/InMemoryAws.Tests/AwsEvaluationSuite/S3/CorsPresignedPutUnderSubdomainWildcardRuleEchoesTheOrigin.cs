// Translated from behavioral/s3/check_cors_presigned_put_under_subdomain_wildcard_rule_echoes_the_origin.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-presigned-put-under-subdomain-wildcard-rule-echoes-the-origin</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsPresignedPutUnderSubdomainWildcardRuleEchoesTheOrigin
{
    private const string Check = "cors-presigned-put-under-subdomain-wildcard-rule-echoes-the-origin";
    private const string Origin = "https://shop.example.org";
    private const string Key = "carts/cart-1.json";
    private static readonly byte[] Body = "{\"items\": 2}"u8.ToArray();

    /// <summary>
    /// A presigned PUT from a subdomain an https://*.example.org rule matches stores the object and
    /// echoes that exact Origin in Allow-Origin with the rule's methods and exposed headers.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-presigned-put-under-subdomain-wildcard-rule-echoes-the-origin")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check);
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration
            {
                Rules = [new CORSRule { AllowedOrigins = ["https://*.example.org"], AllowedMethods = ["GET", "PUT"], ExposeHeaders = ["x-amz-version-id"] }],
            },
        });
        await S3Fidelity.UntilPreflightStatus(
            ctx, S3Fidelity.ObjectUrl(ctx, bucket, Key), new Dictionary<string, string> { ["Origin"] = Origin, ["Access-Control-Request-Method"] = "PUT" }, 200);
        var url = S3Fidelity.Presign(ctx, "PUT", bucket, Key, 300, pathStyle: true);
        var (status, headers, body) = await S3Fidelity.Send(ctx, "PUT", url, new Dictionary<string, string> { ["Origin"] = Origin }, Body);
        Ensure(status == 200, $"the presigned PUT answered {status}: {S3Fidelity.Head(body, 300)}");
        var want = new Dictionary<string, string>
        {
            ["access-control-allow-origin"] = Origin,
            ["access-control-allow-methods"] = "GET, PUT",
            ["access-control-expose-headers"] = "x-amz-version-id",
            ["access-control-allow-credentials"] = "true",
        };
        Ensure(
            S3Fidelity.SameHeaders(S3Fidelity.Cors(headers), want),
            $"the subdomain rule's CORS headers are {Py.Repr(S3Fidelity.Cors(headers))}, expected {Py.Repr(want)}");
        Ensure(S3Fidelity.HeaderTokens(headers.GetValueOrDefault("vary")).SetEquals(S3Fidelity.CorsVary), $"the subdomain rule's Vary is {Py.Repr(headers.GetValueOrDefault("vary"))}");

        using var stored = await s3.GetObjectAsync(bucket, Key);
        var got = await S3Fidelity.ReadBody(stored);
        Ensure(got.SequenceEqual(Body), $"GetObject reads {S3Fidelity.Head(got)} after the presigned PUT, expected {S3Fidelity.Head(Body)}");
        Ensure(stored.ETag == S3Fidelity.Md5ETag(Body), $"the stored ETag is {Py.Repr(stored.ETag)}");
    }
}
