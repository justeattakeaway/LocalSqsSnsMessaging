// Translated from behavioral/s3/check_conc_s3_conditional_writes_complete_multipart_if_none_match_rejected_when_key_appears_mid_upload.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.conc-s3-conditional-writes-complete-multipart-if-none-match-rejected-when-key-appears-mid-upload</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcS3ConditionalWritesCompleteMultipartIfNoneMatchRejectedWhenKeyAppearsMidUpload
{
    private const int Mib = 1024 * 1024;

    /// <summary>
    /// CompleteMultipartUpload If-None-Match:* is evaluated at completion: a key written mid-upload
    /// fails 412 PreconditionFailed and survives intact; on an absent key it assembles the parts.
    /// </summary>
    [Test]
    [DisplayName("s3.conc-s3-conditional-writes-complete-multipart-if-none-match-rejected-when-key-appears-mid-upload")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateCondwBucket(ctx);
        var part1 = S3Fidelity.Pattern("part-one", 5 * Mib);
        var part2 = S3Fidelity.Pattern("part-two-tail", 123_457);

        // --- POSITIVE CONTROL: If-None-Match:* on a key nobody created completes and assembles.
        const string ctrlKey = "control/assembled.bin";
        var ctrlUpload = await S3Fidelity.StartUpload(ctx, s3, bucket, ctrlKey);
        var ctrlParts = await S3Fidelity.UploadParts(s3, bucket, ctrlKey, ctrlUpload, part1, part2);
        var done = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = ctrlKey,
            UploadId = ctrlUpload,
            PartETags = ctrlParts,
            IfNoneMatch = "*",
        });
        Ensure(done.HttpStatusCode == HttpStatusCode.OK, $"If-None-Match:* completion on an absent key returned {(int)done.HttpStatusCode}, expected 200");
        using (var assembled = await s3.GetObjectAsync(bucket, ctrlKey))
        {
            var body = await S3Fidelity.ReadBody(assembled);
            Ensure(
                body.AsSpan().SequenceEqual([.. part1, .. part2]),
                $"the control object is {body.Length} bytes (sha256 {S3Fidelity.Sha256Hex(body)}), want exactly part1+part2 = {part1.Length + part2.Length} bytes");
            Ensure(
                assembled.ETag == done.ETag && done.ETag.EndsWith("-2\"", StringComparison.Ordinal),
                $"GET ETag {Py.Repr(assembled.ETag)} vs completion ETag {Py.Repr(done.ETag)}; want equal and a two-part multipart ETag ending in -2");
        }

        // --- Break exactly one thing: the key is absent at initiation but EXISTS at completion.
        const string key = "contended/raced.bin";
        var upload = await S3Fidelity.StartUpload(ctx, s3, bucket, key);
        var parts = await S3Fidelity.UploadParts(s3, bucket, key, upload, part1, part2);
        var interloper = Encoding.UTF8.GetBytes("interloper-wrote-this-while-the-multipart-upload-was-open");
        var interloperEtag = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(interloper) })).ETag;

        var exc = await FidelityContext.CaptureError(() => s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = upload,
            PartETags = parts,
            IfNoneMatch = "*",
        }));
        Ensure(exc is not null, "CompleteMultipartUpload If-None-Match:* succeeded over a key an interloper created mid-upload; the condition was evaluated at initiation or ignored");
        Ensure(exc!.Code == "PreconditionFailed", $"expected PreconditionFailed, got {Py.Repr(exc.Code)}");
        Ensure(exc.Message == "At least one of the pre-conditions you specified did not hold", exc.Message ?? "None");
        Ensure(exc.StatusCode == HttpStatusCode.PreconditionFailed, $"expected HTTP 412, got {(int)exc.StatusCode}");

        // --- Effect absent: the interloper's object is untouched by bytes, ETag and size.
        using (var got = await s3.GetObjectAsync(bucket, key))
        {
            var now = await S3Fidelity.ReadBody(got);
            Ensure(now.AsSpan().SequenceEqual(interloper), $"the refused completion changed the object: it is {now.Length} bytes, want the {interloper.Length}-byte interloper payload");
            Ensure(got.ETag == interloperEtag, $"object ETag is {Py.Repr(got.ETag)}, want the interloper's {Py.Repr(interloperEtag)}");
        }

        var head = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(head.ContentLength == interloper.Length, $"HeadObject reports {head.ContentLength} bytes, want {interloper.Length}");
    }
}
