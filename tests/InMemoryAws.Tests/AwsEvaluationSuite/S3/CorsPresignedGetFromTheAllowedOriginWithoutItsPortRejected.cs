// Translated from behavioral/s3/check_cors_presigned_get_from_the_allowed_origin_without_its_port_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-presigned-get-from-the-allowed-origin-without-its-port-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsPresignedGetFromTheAllowedOriginWithoutItsPortRejected
{
    private const string Check = "cors-presigned-get-from-the-allowed-origin-without-its-port-rejected";
    private const string Listed = "http://www.example.com:8080";
    private const string SameHostNoPort = "http://www.example.com";
    private const string ObjectKey = "ported/banner.txt";
    private static readonly byte[] ObjectBody = "served to a page on a non-default port"u8.ToArray();

    /// <summary>
    /// An origin matches AllowedOrigins only with its port: a presigned GET from the same host
    /// without the listed port is served with no CORS headers, with the port it carries the rule's.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-presigned-get-from-the-allowed-origin-without-its-port-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check, "sim2real-behav-port-");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = ObjectKey, InputStream = new MemoryStream(ObjectBody), ContentType = "text/plain" });
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration { Rules = [new CORSRule { AllowedOrigins = [Listed], AllowedMethods = ["GET"], AllowedHeaders = ["content-type"] }] },
        });
        var signedRead = S3Fidelity.Presign(ctx, "GET", bucket, ObjectKey, 600);

        // The exact origin is the legal neighbour of the port-less one, and proves the rule is live.
        var allowed = await S3Fidelity.AwaitAnswer(
            ctx,
            "GET",
            signedRead,
            new Dictionary<string, string> { ["Origin"] = Listed },
            a => a.Headers.GetValueOrDefault("access-control-allow-origin") == Listed,
            (a, budget) => $"after {budget:F0}s the listed origin's GET answered {a.Status} with Allow-Origin {Py.Repr(a.Headers.GetValueOrDefault("access-control-allow-origin"))}, expected {Py.Repr(Listed)}");
        Ensure(allowed.Body.SequenceEqual(ObjectBody), $"the GET from the listed origin served {S3Fidelity.Head(allowed.Body)}, expected {S3Fidelity.Head(ObjectBody)}");
        Ensure(
            allowed.Headers.GetValueOrDefault("access-control-allow-methods") == "GET",
            $"the GET from the listed origin answered Access-Control-Allow-Methods {Py.Repr(allowed.Headers.GetValueOrDefault("access-control-allow-methods"))}, expected 'GET'");

        var unported = await S3Fidelity.Send(ctx, "GET", signedRead, new Dictionary<string, string> { ["Origin"] = SameHostNoPort });
        Ensure(unported.Status == 200, $"the GET from the port-less origin answered {unported.Status}, expected 200: {S3Fidelity.Head(unported.Body, 300)}");
        Ensure(unported.Body.SequenceEqual(ObjectBody), $"the GET from the port-less origin served {S3Fidelity.Head(unported.Body)}, expected {S3Fidelity.Head(ObjectBody)}");
        var granted = S3Fidelity.Cors(unported.Headers);
        Ensure(granted.Count == 0, $"the port-less origin was granted {Py.Repr(granted)}, expected no Access-Control-* header");
        Ensure(!unported.Headers.ContainsKey("vary"), $"the port-less origin's answer carried Vary {Py.Repr(unported.Headers.GetValueOrDefault("vary"))}");
    }
}
