// Translated from behavioral/s3/check_conc_s3_conditional_writes_complete_multipart_stale_if_match_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.conc-s3-conditional-writes-complete-multipart-stale-if-match-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcS3ConditionalWritesCompleteMultipartStaleIfMatchRejected
{
    private const int Mib = 1024 * 1024;
    private const string PreconditionMsg = "At least one of the pre-conditions you specified did not hold";

    /// <summary>
    /// CompleteMultipartUpload with an If-Match ETag made stale by a concurrent overwrite is refused with
    /// 412 PreconditionFailed and its exact message and keeps the overwriter's bytes; the same completion
    /// naming the current ETag replaces the object with exactly part1 + part2.
    /// </summary>
    [Test]
    [DisplayName("s3.conc-s3-conditional-writes-complete-multipart-stale-if-match-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateCondwBucket(ctx);
        const string key = "optimistic/document.bin";
        var part1 = S3Fidelity.Pattern("mpu-part-one", 5 * Mib);
        var part2 = S3Fidelity.Pattern("mpu-part-two", 77_777);

        var original = Encoding.UTF8.GetBytes("version-read-by-the-uploader-before-it-started");
        var staleEtag = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(original) })).ETag;

        var upload = await S3Fidelity.StartUpload(ctx, s3, bucket, key);
        var parts = await S3Fidelity.UploadParts(s3, bucket, key, upload, part1, part2);

        // A concurrent writer overwrites the key while the upload is still open.
        var overwrite = Encoding.UTF8.GetBytes("concurrent-writer-replaced-the-object-mid-upload");
        var currentEtag = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(overwrite) })).ETag;
        Ensure(currentEtag != staleEtag, $"the overwrite kept ETag {Py.Repr(currentEtag)}; the check cannot tell stale from current");

        // --- Break exactly one thing: complete conditioned on the ETag that is no longer current.
        var exc = await S3Fidelity.ExpectError(
            () => s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = upload,
                PartETags = parts,
                IfMatch = staleEtag,
            }),
            $"CompleteMultipartUpload If-Match {Py.Repr(staleEtag)} succeeded although the current ETag is {Py.Repr(currentEtag)}; the concurrent writer's data was silently discarded");
        Ensure(exc.Code == "PreconditionFailed", $"expected PreconditionFailed, got {Py.Repr(exc.Code)}");
        Ensure(exc.Message == PreconditionMsg, $"expected message {Py.Repr(PreconditionMsg)}, got {Py.Repr(exc.Message)}");
        Ensure(exc.StatusCode == HttpStatusCode.PreconditionFailed, $"expected HTTP 412, got {(int)exc.StatusCode}");

        // --- Effect absent: the concurrent writer's object survives exactly.
        using (var got = await s3.GetObjectAsync(bucket, key))
        {
            var now = await S3Fidelity.ReadBody(got);
            Ensure(now.AsSpan().SequenceEqual(overwrite), $"the refused completion changed the object to {now.Length} bytes; want the {overwrite.Length}-byte concurrent overwrite");
            Ensure(got.ETag == currentEtag, $"object ETag is {Py.Repr(got.ETag)}, want the untouched {Py.Repr(currentEtag)}");
        }

        // --- POSITIVE CONTROL: a fresh upload conditioned on the CURRENT ETag replaces the object.
        var retryUpload = await S3Fidelity.StartUpload(ctx, s3, bucket, key);
        var retryParts = await S3Fidelity.UploadParts(s3, bucket, key, retryUpload, part1, part2);
        var done = await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = retryUpload,
            PartETags = retryParts,
            IfMatch = currentEtag,
        });
        Ensure(done.HttpStatusCode == HttpStatusCode.OK, $"If-Match with the current ETag returned {(int)done.HttpStatusCode}");
        using var after = await s3.GetObjectAsync(bucket, key);
        var body = await S3Fidelity.ReadBody(after);
        Ensure(
            body.AsSpan().SequenceEqual([.. part1, .. part2]),
            $"after the conditional completion the object is {body.Length} bytes (sha256 {S3Fidelity.Sha256Hex(body)}), want exactly part1+part2");
        Ensure(
            after.ETag == done.ETag && done.ETag != currentEtag,
            $"GET ETag {Py.Repr(after.ETag)}, completion ETag {Py.Repr(done.ETag)}, previous {Py.Repr(currentEtag)}: want GET == completion and both new");
    }
}
