// Translated from behavioral/s3/check_s3_ssec_and_endpoint_enforcement_secure_transport_deny_policy_refuses_plaintext_http_and_allows_https.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-ssec-and-endpoint-enforcement-secure-transport-deny-policy-refuses-plaintext-http-and-allows-https</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3SsecAndEndpointEnforcementSecureTransportDenyPolicyRefusesPlaintextHttpAndAllowsHttps
{
    /// <summary>
    /// A SecureTransport=false Deny policy refuses plaintext http:// read, list and write with an explicit-
    /// deny AccessDenied while each https:// twin is served; removing it restores http.
    /// </summary>
    [Test]
    [KnownFidelityGap(FidelityGap.SecureTransport, "aws:SecureTransport is always true, so the Deny-unless-TLS bucket policy never refuses the plain-HTTP requests")]
    [DisplayName("s3.s3-ssec-and-endpoint-enforcement-secure-transport-deny-policy-refuses-plaintext-http-and-allows-https")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        const string check = "s3-ssec-and-endpoint-enforcement-secure-transport-deny-policy-refuses-plaintext-http-and-allows-https";
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.EdgeBucket(ctx, check);
        var body = "served only over tls once the policy lands"u8.ToArray();
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "doc.txt", InputStream = new MemoryStream(body) });
        var getUrl = S3Fidelity.Presign(ctx, "GET", bucket, "doc.txt", 600, pathStyle: true);
        var listUrl = S3Fidelity.Presign(ctx, "GET", bucket, null, 600, query: [new("list-type", "2")], pathStyle: true);
        var putUrl = S3Fidelity.Presign(ctx, "PUT", bucket, "written.txt", 600, pathStyle: true);

        // Control: plaintext http works before the policy, so the refusal below is the policy's.
        var (status, _, raw) = await S3Fidelity.Send(ctx, "GET", S3Fidelity.AsHttp(getUrl));
        Ensure(
            status == 200 && raw.SequenceEqual(body),
            $"control: http:// GET before the policy returned HTTP {status} {S3Fidelity.Head(raw)}; plaintext " +
            "transport must work before the policy for the refusal to be attributable to it");

        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "DenyInsecureTransport",
                ["Effect"] = "Deny",
                ["Principal"] = "*",
                ["Action"] = "s3:*",
                ["Resource"] = new JsonArray($"arn:aws:s3:::{bucket}", $"arn:aws:s3:::{bucket}/*"),
                ["Condition"] = new JsonObject { ["Bool"] = new JsonObject { ["aws:SecureTransport"] = "false" } },
            }),
        };
        await s3.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = bucket, Policy = policy.ToJsonString() });
        ctx.OnCleanup(() => s3.DeleteBucketPolicyAsync(new DeleteBucketPolicyRequest { BucketName = bucket }));

        // GET over http:// is refused with an explicit-deny AccessDenied.
        (status, _, raw) = await S3Fidelity.AwaitStatus(ctx, () => S3Fidelity.Send(ctx, "GET", S3Fidelity.AsHttp(getUrl)), want: 403);
        Ensure(
            S3Fidelity.XmlCodeMessage(raw).Code == "AccessDenied",
            $"http:// GET under the SecureTransport Deny returned code {Py.Repr(S3Fidelity.XmlCodeMessage(raw).Code)}, expected " +
            $"AccessDenied — body {S3Fidelity.Head(raw, 300)}");
        Ensure(Encoding.UTF8.GetString(raw).Contains("explicit deny", StringComparison.Ordinal), $"the http:// refusal does not name the explicit deny: {S3Fidelity.Head(raw, 400)}");
        Ensure(raw.AsSpan().IndexOf(body) < 0, "the refused http:// GET leaked the object's bytes");

        // The https:// twin of the same GET is served.
        (status, _, raw) = await S3Fidelity.Send(ctx, "GET", getUrl);
        Ensure(
            status == 200 && raw.SequenceEqual(body),
            $"the https:// twin of the refused GET returned HTTP {status} {S3Fidelity.Head(raw)}; expected 200 " +
            "with the object's bytes");

        // List: http refused, https served.
        (status, _, raw) = await S3Fidelity.Send(ctx, "GET", S3Fidelity.AsHttp(listUrl));
        Ensure(
            status == 403 && S3Fidelity.XmlCodeMessage(raw).Code == "AccessDenied",
            $"http:// ListObjectsV2 returned HTTP {status} code {Py.Repr(S3Fidelity.XmlCodeMessage(raw).Code)}; expected 403 " +
            "AccessDenied");
        (status, _, raw) = await S3Fidelity.Send(ctx, "GET", listUrl);
        Ensure(status == 200, $"https:// ListObjectsV2 returned HTTP {status}: {S3Fidelity.Head(raw, 300)}");
        var keys = XElement.Parse(Encoding.UTF8.GetString(raw)).Descendants().Where(e => e.Name.LocalName == "Key").Select(e => e.Value).ToList();
        Ensure(keys.SequenceEqual(["doc.txt"]), $"https:// ListObjectsV2 listed {Py.Repr(keys)}, expected ['doc.txt']");

        // Write: http refused and nothing lands; https writes.
        (status, _, raw) = await S3Fidelity.Send(ctx, "PUT", S3Fidelity.AsHttp(putUrl), data: "plaintext write"u8.ToArray());
        Ensure(
            status == 403 && S3Fidelity.XmlCodeMessage(raw).Code == "AccessDenied",
            $"http:// PUT returned HTTP {status} code {Py.Repr(S3Fidelity.XmlCodeMessage(raw).Code)}; expected 403 AccessDenied");
        var (code, _, msg) = await S3Fidelity.ErrorTriple(() => s3.GetObjectAsync(bucket, "written.txt"));
        Ensure((code, msg) == ("NoSuchKey", "The specified key does not exist."), $"the refused http:// PUT created an object ({Py.Repr(code)}: {Py.Repr(msg)})");
        (status, _, raw) = await S3Fidelity.Send(ctx, "PUT", putUrl, data: "tls write"u8.ToArray());
        Ensure(status == 200, $"the https:// twin of the refused PUT returned HTTP {status}: {S3Fidelity.Head(raw, 300)}");
        var got = await S3Fidelity.GetBytes(s3, bucket, "written.txt");
        Ensure(got.SequenceEqual("tls write"u8.ToArray()), $"the https:// PUT read back as {S3Fidelity.Head(got)}");

        // Restore: removing the policy re-admits plaintext http.
        await s3.DeleteBucketPolicyAsync(new DeleteBucketPolicyRequest { BucketName = bucket });
        (_, _, raw) = await S3Fidelity.AwaitStatus(ctx, () => S3Fidelity.Send(ctx, "GET", S3Fidelity.AsHttp(getUrl)), want: 200);
        Ensure(raw.SequenceEqual(body), $"http:// GET after the policy was removed returned {S3Fidelity.Head(raw)}");
    }
}
