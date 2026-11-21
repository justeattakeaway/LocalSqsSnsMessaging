// Translated from behavioral/s3/check_cors_presigned_delete_with_origin_removes_the_object_and_grants_the_rule.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-presigned-delete-with-origin-removes-the-object-and-grants-the-rule</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsPresignedDeleteWithOriginRemovesTheObjectAndGrantsTheRule
{
    private const string Check = "cors-presigned-delete-with-origin-removes-the-object-and-grants-the-rule";
    private const string Site = "https://console.example.com";
    private const string ObjectKey = "drafts/discarded.txt";
    private static readonly byte[] ObjectBody = "a draft a page's script throws away"u8.ToArray();

    /// <summary>
    /// A presigned DELETE sent with an Origin a DELETE rule allows answers 204 with that rule's
    /// CORS headers, and the object it addressed is gone from the bucket afterwards.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-presigned-delete-with-origin-removes-the-object-and-grants-the-rule")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check, "sim2real-behav-del-");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = ObjectKey, InputStream = new MemoryStream(ObjectBody), ContentType = "text/plain" });
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration { Rules = [new CORSRule { AllowedOrigins = [Site], AllowedMethods = ["DELETE"], AllowedHeaders = ["content-type"] }] },
        });

        // The preflight is the one way to see the rule is live without spending the delete on it.
        var cleared = await S3Fidelity.AwaitAnswer(
            ctx,
            "OPTIONS",
            S3Fidelity.ObjectUrl(ctx, bucket, ObjectKey),
            new Dictionary<string, string> { ["Origin"] = Site, ["Access-Control-Request-Method"] = "DELETE" },
            a => a.Status == 200,
            (a, budget) => $"the DELETE preflight was still answering {a.Status} after {budget:F0}s, never 200: {S3Fidelity.Head(a.Body, 300)}");
        Ensure(
            cleared.Headers.GetValueOrDefault("access-control-allow-methods") == "DELETE",
            $"the preflight answered Access-Control-Allow-Methods {Py.Repr(cleared.Headers.GetValueOrDefault("access-control-allow-methods"))}, expected 'DELETE'");

        var signedDelete = S3Fidelity.Presign(ctx, "DELETE", bucket, ObjectKey, 600);
        var (status, answered, content) = await S3Fidelity.Send(ctx, "DELETE", signedDelete, new Dictionary<string, string> { ["Origin"] = Site });
        Ensure(status == 204, $"the presigned DELETE answered {status}, expected 204: {S3Fidelity.Head(content, 300)}");
        Ensure(
            answered.GetValueOrDefault("access-control-allow-origin") == Site,
            $"the presigned DELETE answered Access-Control-Allow-Origin {Py.Repr(answered.GetValueOrDefault("access-control-allow-origin"))}, expected {Py.Repr(Site)}");
        Ensure(
            answered.GetValueOrDefault("access-control-allow-methods") == "DELETE",
            $"the presigned DELETE answered Access-Control-Allow-Methods {Py.Repr(answered.GetValueOrDefault("access-control-allow-methods"))}, expected 'DELETE'");
        Ensure(content.Length == 0, $"the presigned DELETE returned a body: {S3Fidelity.Head(content, 300)}");

        var refusal = await S3Fidelity.ErrorPair(() => s3.GetObjectAsync(bucket, ObjectKey), "the read of the deleted key succeeded, where a refusal was expected");
        Ensure(
            refusal == ("NoSuchKey", "The specified key does not exist."),
            $"reading the deleted key answered {S3Fidelity.Repr(refusal)}, so the browser's DELETE kept it");
        var remaining = await S3CoreFidelity.ListAllKeys(s3, bucket);
        Ensure(!remaining.Contains(ObjectKey), $"the deleted key is still listed among this bucket's {remaining.Count} object(s)");
    }
}
