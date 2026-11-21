// Translated from behavioral/s3/check_cors_browser_form_post_upload_with_origin_stores_the_bytes_and_grants_the_rule.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-browser-form-post-upload-with-origin-stores-the-bytes-and-grants-the-rule</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsBrowserFormPostUploadWithOriginStoresTheBytesAndGrantsTheRule
{
    private const string Check = "cors-browser-form-post-upload-with-origin-stores-the-bytes-and-grants-the-rule";
    private const string Site = "https://uploader.example.com";
    private const string ObjectKey = "inbox/from-a-form.txt";
    private static readonly byte[] ObjectBody = "bytes a browser form posted straight to S3"u8.ToArray();

    /// <summary>
    /// A browser form POST to a presigned policy, sent with an Origin a POST rule allows, stores
    /// the bytes and answers that rule's CORS headers, which is all a page's script can read.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-browser-form-post-upload-with-origin-stores-the-bytes-and-grants-the-rule")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check, "sim2real-behav-form-");
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration { Rules = [new CORSRule { AllowedOrigins = [Site], AllowedMethods = ["POST"], AllowedHeaders = ["content-type"] }] },
        });

        // The preflight shows the rule is in effect before the one upload is spent on it.
        var (_, cleared, _) = await S3Fidelity.AwaitAnswer(
            ctx,
            "OPTIONS",
            S3Fidelity.ObjectUrl(ctx, bucket, ObjectKey),
            new Dictionary<string, string> { ["Origin"] = Site, ["Access-Control-Request-Method"] = "POST" },
            a => a.Status == 200,
            (a, budget) => $"the form preflight was still answering {a.Status} after {budget:F0}s, never 200: {S3Fidelity.Head(a.Body, 300)}");
        Ensure(
            cleared.GetValueOrDefault("access-control-allow-methods") == "POST",
            $"the form preflight answered Access-Control-Allow-Methods {Py.Repr(cleared.GetValueOrDefault("access-control-allow-methods"))}, expected 'POST'");

        var form = await s3.CreatePresignedPostAsync(new CreatePresignedPostRequest
        {
            BucketName = bucket,
            Key = ObjectKey,
            Expires = ctx.Time.GetUtcNow().UtcDateTime.AddSeconds(600),
        });
        var (status, answered, content) = await S3Fidelity.PostFormAnswer(
            ctx, form.Url, [.. form.Fields], "from-a-form.txt", ObjectBody, extraHeaders: new Dictionary<string, string> { ["Origin"] = Site });
        Ensure(status == 204, $"the form POST answered {status}, expected 204: {S3Fidelity.Head(content, 300)}");
        Ensure(
            answered.GetValueOrDefault("access-control-allow-origin") == Site,
            $"the form POST answered Access-Control-Allow-Origin {Py.Repr(answered.GetValueOrDefault("access-control-allow-origin"))}, expected {Py.Repr(Site)}");
        Ensure(
            answered.GetValueOrDefault("access-control-allow-methods") == "POST",
            $"the form POST answered Access-Control-Allow-Methods {Py.Repr(answered.GetValueOrDefault("access-control-allow-methods"))}, expected 'POST'");

        var body = await S3Fidelity.GetBytes(s3, bucket, ObjectKey);
        Ensure(body.SequenceEqual(ObjectBody), $"the object the form uploaded holds {S3Fidelity.Head(body)}, expected {S3Fidelity.Head(ObjectBody)}");
        var keys = (await S3CoreFidelity.ListAllKeys(s3, bucket)).ToHashSet(StringComparer.Ordinal);
        Ensure(
            keys.SetEquals([ObjectKey]),
            $"the form POST left this bucket holding {Py.Repr(keys.Order(StringComparer.Ordinal).ToList())}, expected only the signed key");
    }
}
