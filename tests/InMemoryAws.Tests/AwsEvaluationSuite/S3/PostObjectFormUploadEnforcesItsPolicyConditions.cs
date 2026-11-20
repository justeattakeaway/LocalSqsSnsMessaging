// Translated from behavioral/s3/check_post_object_form_upload_enforces_its_policy_conditions.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.post-object-form-upload-enforces-its-policy-conditions</summary>
[Category("AwsEvaluationSuite")]
public sealed class PostObjectFormUploadEnforcesItsPolicyConditions
{
    /// <summary>
    /// A POST Object form upload is admitted or refused by its signed policy document: a key outside the
    /// starts-with prefix and a file over content-length-range are refused and not stored, while the same
    /// form with a conforming key and size succeeds.
    /// </summary>
    [Test]
    [DisplayName("s3.post-object-form-upload-enforces-its-policy-conditions")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        const string prefix = "uploads/";
        const int maxBytes = 4096;
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);
        var postUrl = await BucketPostUrl(s3, bucket);
        var form = SignedPostForm(ctx, bucket, prefix, maxBytes);

        // Positive control: a conforming key under the prefix, a body inside the size range.
        var goodKey = $"{prefix}accepted-{Guid.NewGuid():N}.txt";
        var goodBody = "post-object-conforming-upload"u8.ToArray();
        var (status, body) = await S3Fidelity.PostForm(ctx, postUrl, With(form, goodKey), "payload.bin", goodBody, "application/octet-stream");
        Ensure(
            status == 201,
            $"the conforming POST Object was answered {status} with {S3Fidelity.Head(body)}; a POST whose form " +
            "satisfies every policy condition and carries success_action_status=201 must be accepted " +
            "with 201");
        Ensure(
            Encoding.UTF8.GetString(body).Contains($"<Key>{goodKey}</Key>", StringComparison.Ordinal),
            $"the 201 POST response body did not name the uploaded key {Py.Repr(goodKey)}: {S3Fidelity.Head(body, 300)}");
        var stored = await S3Fidelity.GetBytes(s3, bucket, goodKey);
        Ensure(
            stored.SequenceEqual(goodBody),
            $"GetObject returned {S3Fidelity.Head(stored)} for the POST-uploaded key {Py.Repr(goodKey)}; the form part " +
            $"carried {S3Fidelity.Head(goodBody)}");

        // Break exactly one thing: a key outside the starts-with prefix.
        var badKey = $"escaped/{Guid.NewGuid():N}.txt";
        (status, body) = await S3Fidelity.PostForm(ctx, postUrl, With(form, badKey), "payload.bin", goodBody, "application/octet-stream");
        Ensure(
            status == 403,
            $"POSTing key {Py.Repr(badKey)} against a policy that pins ['starts-with', '$key', " +
            $"{Py.Repr(prefix)}] was answered {status} with {S3Fidelity.Head(body)}; expected 403 — the policy " +
            "condition is not being evaluated, so any holder of the form can write anywhere in the " +
            "bucket");
        Ensure(
            Encoding.UTF8.GetString(body).Contains("<Code>AccessDenied</Code>", StringComparison.Ordinal),
            $"the refused POST returned no AccessDenied code: {S3Fidelity.Head(body, 300)}");
        Ensure(
            (await S3Fidelity.HeadError(s3, bucket, badKey)).Code == "404",
            $"the key {Py.Repr(badKey)} exists after a POST that was refused with 403 — the refusal did not " +
            "prevent the write");

        // Break exactly one other thing: a body one byte over content-length-range.
        var oversizeKey = $"{prefix}oversize-{Guid.NewGuid():N}.txt";
        var oversizeBody = S3Fidelity.Repeat('x', maxBytes + 1);
        (status, body) = await S3Fidelity.PostForm(ctx, postUrl, With(form, oversizeKey), "payload.bin", oversizeBody, "application/octet-stream");
        Ensure(
            status == 400,
            $"POSTing {oversizeBody.Length} bytes against a policy whose content-length-range maximum " +
            $"is {maxBytes} was answered {status} with {S3Fidelity.Head(body)}; expected 400");
        Ensure(
            Encoding.UTF8.GetString(body).Contains("<Code>EntityTooLarge</Code>", StringComparison.Ordinal),
            $"the oversize POST returned no EntityTooLarge code: {S3Fidelity.Head(body, 300)}");
        Ensure(
            (await S3Fidelity.HeadError(s3, bucket, oversizeKey)).Code == "404",
            $"the key {Py.Repr(oversizeKey)} exists after a POST refused for exceeding " +
            $"content-length-range={maxBytes}");

        // Restore: the same signed policy still admits a conforming upload after the refusals.
        var restoredKey = $"{prefix}restored-{Guid.NewGuid():N}.txt";
        var restoredBody = "post-object-after-two-refusals"u8.ToArray();
        (status, body) = await S3Fidelity.PostForm(ctx, postUrl, With(form, restoredKey), "payload.bin", restoredBody, "application/octet-stream");
        Ensure(
            status == 201,
            $"the restored conforming POST was answered {status} with {S3Fidelity.Head(body)}; the same signed " +
            "policy accepted an upload before the two refusals and must accept one after them");
        Ensure(
            (await S3Fidelity.GetBytes(s3, bucket, restoredKey)).SequenceEqual(restoredBody),
            $"GetObject did not return {S3Fidelity.Head(restoredBody)} for {Py.Repr(restoredKey)} after the restore leg");

        var listed = ((await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket })).S3Objects ?? []).Select(o => o.Key).Order(StringComparer.Ordinal).ToList();
        List<string> expected = [.. new[] { goodKey, restoredKey }.Order(StringComparer.Ordinal)];
        Ensure(
            listed.SequenceEqual(expected),
            $"the bucket lists {Py.Repr(listed)}; expected exactly the two accepted keys " +
            $"{Py.Repr(expected)} — a refused POST left an object behind");
    }

    /// <summary><c>dict(form, key=key)</c>.</summary>
    private static List<KeyValuePair<string, string>> With(List<KeyValuePair<string, string>> form, string key) =>
        [.. form, new("key", key)];

    /// <summary>
    /// <c>_bucket_post_url(ctx, s3, bucket)</c>: the POST Object target (bucket root, trailing slash): a
    /// presigned PutObject URL with its query and probe key stripped.
    /// </summary>
    private static async Task<string> BucketPostUrl(IAmazonS3 s3, string bucket)
    {
        const string probeKey = "post-object-endpoint-probe";
        var presigned = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest { BucketName = bucket, Key = probeKey, Verb = HttpVerb.PUT, Expires = DateTime.UtcNow.AddSeconds(60) });
        var bare = S3Fidelity.WithoutQuery(presigned);
        Ensure(
            bare.EndsWith("/" + probeKey, StringComparison.Ordinal),
            $"cannot derive the bucket URL: the presigned PutObject URL {Py.Repr(bare)} does not end in /{probeKey}");
        return bare[..^probeKey.Length];
    }

    /// <summary>
    /// <c>_signed_post_form(ctx, bucket, prefix, max_bytes)</c>: a SigV4-signed POST Object policy from
    /// the live clock, and every form field except <c>key</c> and <c>file</c>. Each field has a matching
    /// policy condition: POST Object refuses a form carrying any field the policy does not cover.
    /// </summary>
    private static List<KeyValuePair<string, string>> SignedPostForm(FidelityContext ctx, string bucket, string prefix, int maxBytes)
    {
        var now = ctx.Time.GetUtcNow().UtcDateTime;
        var amzDate = S3Fidelity.AmzDate(now);
        var datestamp = amzDate[..8];
        var expiration = S3Fidelity.IsoSeconds(now.AddMinutes(20));
        var credential = $"{S3Fidelity.AccessKey}/{datestamp}/{ctx.Region}/s3/aws4_request";
        var conditions = new JsonArray(
            new JsonObject { ["bucket"] = bucket },
            new JsonArray("starts-with", "$key", prefix),
            new JsonObject { ["x-amz-algorithm"] = "AWS4-HMAC-SHA256" },
            new JsonObject { ["x-amz-credential"] = credential },
            new JsonObject { ["x-amz-date"] = amzDate },
            new JsonObject { ["success_action_status"] = "201" },
            new JsonArray("content-length-range", 1, maxBytes));
        var policy = Convert.ToBase64String(Encoding.UTF8.GetBytes(new JsonObject { ["expiration"] = expiration, ["conditions"] = conditions }.ToJsonString()));
        var signature = S3Fidelity.HmacHex(S3Fidelity.SigningKey(S3Fidelity.SecretKey, datestamp, ctx.Region), policy);
        return
        [
            new("x-amz-algorithm", "AWS4-HMAC-SHA256"),
            new("x-amz-credential", credential),
            new("x-amz-date", amzDate),
            new("success_action_status", "201"),
            new("policy", policy),
            new("x-amz-signature", signature),
        ];
    }
}
