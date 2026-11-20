// Translated from behavioral/s3/check_presigned_post_form_upload_substitutes_filename_and_matches_content_type.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.presigned-post-form-upload-substitutes-filename-and-matches-content-type</summary>
[Category("AwsEvaluationSuite")]
public sealed class PresignedPostFormUploadSubstitutesFilenameAndMatchesContentType
{
    /// <summary>
    /// A presigned POST form upload substitutes ${filename} into the key server-side. The stored object's
    /// bytes and Content-Type match the form, and no literal-token key is written.
    /// </summary>
    [Test]
    [DisplayName("s3.presigned-post-form-upload-substitutes-filename-and-matches-content-type")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);
        const string prefix = "uploads/";
        const string contentType = "text/plain";
        var presigned = await s3.CreatePresignedPostAsync(new CreatePresignedPostRequest
        {
            BucketName = bucket,
            Key = prefix + "${filename}",
            Fields = new Dictionary<string, string> { ["Content-Type"] = contentType, ["success_action_status"] = "201" },
            Conditions =
            [
                S3PostCondition.ExactMatch("success_action_status", "201"),
                S3PostCondition.StartsWith("key", prefix),
                S3PostCondition.ExactMatch("Content-Type", contentType),
            ],
            Expires = ctx.Time.GetUtcNow().UtcDateTime.AddSeconds(120),
        });
        var (url, fields) = (presigned.Url, presigned.Fields);
        Ensure(
            fields.GetValueOrDefault("key") == prefix + "${filename}",
            $"generate_presigned_post returned key field {Py.Repr(fields.GetValueOrDefault("key"))}; expected the literal " +
            $"token {Py.Repr(prefix + "${filename}")} for the client to substitute nothing itself");

        var uploadedName = $"report-{Guid.NewGuid():N}.txt";
        var bodyBytes = "quarterly totals: 42, 17, 9"u8.ToArray();
        var (status, respBody) = await S3Fidelity.PostForm(ctx, url, [.. fields], uploadedName, bodyBytes, contentType);
        Ensure(
            status == 201,
            $"the POST was answered {status} with {S3Fidelity.Head(respBody, 300)}; a form satisfying every " +
            "policy condition and carrying success_action_status=201 must be accepted with 201");
        var response = Encoding.UTF8.GetString(respBody);
        var expectedKey = prefix + uploadedName;
        Ensure(
            response.Contains($"<Key>{expectedKey}</Key>", StringComparison.Ordinal),
            $"the 201 PostResponse body did not substitute {Py.Repr(uploadedName)} into the key: " +
            $"{S3Fidelity.Head(respBody, 400)}; expected key {Py.Repr(expectedKey)}");
        Ensure(
            response.Contains($"<Bucket>{bucket}</Bucket>", StringComparison.Ordinal),
            $"the 201 PostResponse body did not name bucket {Py.Repr(bucket)}: {S3Fidelity.Head(respBody, 400)}");
        Ensure(response.Contains("<ETag>", StringComparison.Ordinal), $"the 201 PostResponse body carried no <ETag>: {S3Fidelity.Head(respBody, 400)}");

        var head = await s3.GetObjectMetadataAsync(bucket, expectedKey);
        Ensure(
            head.ContentLength == bodyBytes.Length,
            $"HeadObject reports ContentLength={head.ContentLength}; the form carried {bodyBytes.Length} bytes");
        Ensure(
            head.Headers.ContentType == contentType,
            $"HeadObject reports ContentType={Py.Repr(head.Headers.ContentType)}; the form's Content-Type field " +
            $"carried {Py.Repr(contentType)}");
        var stored = await S3Fidelity.GetBytes(s3, bucket, expectedKey);
        Ensure(stored.SequenceEqual(bodyBytes), $"GetObject returned {S3Fidelity.Head(stored)} for {Py.Repr(expectedKey)}; the form part carried {S3Fidelity.Head(bodyBytes)}");

        var literalKey = prefix + "${filename}";
        Ensure(
            (await S3Fidelity.HeadError(s3, bucket, literalKey)).Code == "404",
            $"a key named after the unsubstituted literal token {Py.Repr(literalKey)} exists — " +
            "${filename} substitution did not happen, or happened to the wrong key");
    }
}
