using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;

namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>What the <c>s3_ssec_and_endpoint_enforcement</c> ports share.</summary>
public static partial class S3Fidelity
{
    /// <summary>S3's refusal of a read of an SSE-C object without its key.</summary>
    public const string StoredSse =
        "The object was stored using a form of Server Side Encryption. The correct parameters must be provided to retrieve the object.";

    /// <summary>S3's refusal of an SSE-C key that is not the one the object was written with.</summary>
    public const string ParamsMismatch = "The provided encryption parameters did not match the ones used originally.";

    /// <summary>S3's refusal of a part request without the SSE-C key its upload was initiated with.</summary>
    public const string InitiateEncrypted =
        "The multipart upload initiate requested encryption. Subsequent part requests must include the appropriate encryption parameters.";

    /// <summary>S3's refusal of a read of an SSE-C object with the wrong key.</summary>
    public const string WrongSecret = "Requests specifying Server Side Encryption with Customer provided keys must provide the correct secret key.";

    /// <summary>
    /// <c>_new_bucket(ctx, s3)</c> of the endpoint-enforcement checks: a <c>sim2real-s3edge-</c> bucket,
    /// tagged with the check's name.
    /// </summary>
    public static async Task<string> EdgeBucket(FidelityContext ctx, string check)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(check);
        var name = await NewBucket(ctx, "sim2real-s3edge-");
        await ctx.S3.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = name,
            TagSet = [new Tag { Key = "sim2real:managed", Value = "true" }, new Tag { Key = "sim2real:check", Value = check.Length > 256 ? check[..256] : check }],
        });
        return name;
    }

    /// <summary><c>_allow_ssec(s3, bucket)</c>: opts the bucket out of the SSE-C upload block, and checks the opt-out is stored.</summary>
    public static async Task AllowSsec(IAmazonS3 s3, string bucket)
    {
        ArgumentNullException.ThrowIfNull(s3);
        await s3.PutBucketEncryptionAsync(new PutBucketEncryptionRequest
        {
            BucketName = bucket,
            ServerSideEncryptionConfiguration = new ServerSideEncryptionConfiguration
            {
                ServerSideEncryptionRules =
                [
                    new ServerSideEncryptionRule
                    {
                        ServerSideEncryptionByDefault = new ServerSideEncryptionByDefault { ServerSideEncryptionAlgorithm = ServerSideEncryptionMethod.AES256 },
                        BlockedEncryptionTypes = new BlockedEncryptionTypes { EncryptionType = ["NONE"] },
                    },
                ],
            },
        });
        var rules = (await s3.GetBucketEncryptionAsync(new GetBucketEncryptionRequest { BucketName = bucket })).ServerSideEncryptionConfiguration.ServerSideEncryptionRules;
        var blocked = rules[0].BlockedEncryptionTypes?.EncryptionType?.Select(e => e.ToString()).ToList();
        FidelityAssert.Ensure(blocked is ["NONE"], $"bucket still blocks {Py.Repr(blocked)} after the SSE-C opt-out");
    }

    /// <summary>The SSE-C headers for a customer key, as a raw request sends them.</summary>
    public static Dictionary<string, string> SseCustomerHeaders(byte[] key) => new()
    {
        ["x-amz-server-side-encryption-customer-algorithm"] = "AES256",
        ["x-amz-server-side-encryption-customer-key"] = Convert.ToBase64String(key),
        ["x-amz-server-side-encryption-customer-key-MD5"] = Convert.ToBase64String(MD5.HashData(key)),
    };

    /// <summary><c>_error(fn)</c> of these checks: the (code, HTTP status, message) of the refusal; a success fails.</summary>
    public static async Task<(string? Code, int Status, string? Message)> ErrorTriple(Func<Task> call)
    {
        var error = await Error(call, "the call was expected to be refused and it succeeded");
        return (error.Code, (int)error.StatusCode, error.Message);
    }

    /// <summary><c>_html_error_fields(body)</c>: the <c>&lt;li&gt;Name: value&lt;/li&gt;</c> fields of a website HTML error page.</summary>
    public static Dictionary<string, string> HtmlErrorFields(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match item in Regex.Matches(System.Text.Encoding.UTF8.GetString(body), "<li>(.*?)</li>", RegexOptions.Singleline, TimeSpan.FromSeconds(1)))
        {
            var text = item.Groups[1].Value;
            var separator = text.IndexOf(": ", StringComparison.Ordinal);
            if (separator >= 0)
            {
                fields[text[..separator]] = text[(separator + 2)..];
            }
        }

        return fields;
    }

    /// <summary><c>key</c> as the base64 boto3 sends in <c>SSECustomerKey</c>.</summary>
    public static string B64(byte[] raw) => Convert.ToBase64String(raw);
}
