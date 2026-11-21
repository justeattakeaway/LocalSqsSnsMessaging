// Translated from behavioral/s3/check_annotation_object_if_match_requires_the_current_etag.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.annotation-object-if-match-requires-the-current-etag</summary>
[Category("AwsEvaluationSuite")]
public sealed class AnnotationObjectIfMatchRequiresTheCurrentEtag
{
    private static readonly (string Code, string Message) StaleObjectIfMatch =
        ("PreconditionFailed", "At least one of the pre-conditions you specified did not hold");

    /// <summary>
    /// PutObjectAnnotation and DeleteObjectAnnotation refuse an ObjectIfMatch that names a prior
    /// ETag once the object has since changed, leaving the annotation as it was.
    /// </summary>
    [Test]
    [DisplayName("s3.annotation-object-if-match-requires-the-current-etag")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-annot-ifmatch-");

        const string key = "catalog-entry.json";
        const string nameKept = "price-tier";
        const string nameOther = "discount-code";
        var payloadKept = "tier-standard"u8.ToArray();
        var payloadRejected = "code-never-written"u8.ToArray();
        var payloadAccepted = "code-autumn-sale"u8.ToArray();

        var staleEtag = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "{\"sku\":\"A1\"}" })).ETag;
        var currentEtag = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "{\"sku\":\"A1\",\"rev\":2}" })).ETag;
        Ensure(currentEtag != staleEtag, $"overwriting the object must change its ETag, still {Py.Repr(currentEtag)}");

        await S3Fidelity.PutAnnotation(s3, bucket, key, nameKept, payloadKept);

        await ExpectError(
            () => s3.PutObjectAnnotationAsync(new PutObjectAnnotationRequest
            {
                BucketName = bucket,
                Key = key,
                AnnotationName = nameOther,
                AnnotationPayload = new MemoryStream(payloadRejected),
                ObjectIfMatch = staleEtag,
            }),
            StaleObjectIfMatch.Code, StaleObjectIfMatch.Message);
        await ExpectError(
            () => S3Fidelity.GetAnnotation(s3, bucket, key, nameOther),
            "NoSuchAnnotation", "The specified annotation does not exist.");
        var keptPayload = await S3Fidelity.AnnotationPayload(s3, bucket, key, nameKept);
        Ensure(
            keptPayload.SequenceEqual(payloadKept),
            $"the refused Put must not disturb {Py.Repr(nameKept)}, expected {Py.Repr(payloadKept)}, got {Py.Repr(keptPayload)}");

        // The control: the same call with the object's current ETag succeeds.
        await s3.PutObjectAnnotationAsync(new PutObjectAnnotationRequest
        {
            BucketName = bucket,
            Key = key,
            AnnotationName = nameOther,
            AnnotationPayload = new MemoryStream(payloadAccepted),
            ObjectIfMatch = currentEtag,
        });
        var acceptedPayload = await S3Fidelity.AnnotationPayload(s3, bucket, key, nameOther);
        Ensure(
            acceptedPayload.SequenceEqual(payloadAccepted),
            $"a Put with the current ETag should have written {Py.Repr(payloadAccepted)}, got {Py.Repr(acceptedPayload)}");

        // The same precondition on Delete: a stale ObjectIfMatch is refused and leaves it in place.
        await ExpectError(
            () => s3.DeleteObjectAnnotationAsync(new DeleteObjectAnnotationRequest
            {
                BucketName = bucket,
                Key = key,
                AnnotationName = nameKept,
                ObjectIfMatch = staleEtag,
            }),
            StaleObjectIfMatch.Code, StaleObjectIfMatch.Message);
        var stillTherePayload = await S3Fidelity.AnnotationPayload(s3, bucket, key, nameKept);
        Ensure(
            stillTherePayload.SequenceEqual(payloadKept),
            $"the refused Delete must leave {Py.Repr(nameKept)} intact, expected {Py.Repr(payloadKept)}, got {Py.Repr(stillTherePayload)}");

        // The control: the same Delete with the current ETag succeeds.
        await s3.DeleteObjectAnnotationAsync(new DeleteObjectAnnotationRequest
        {
            BucketName = bucket,
            Key = key,
            AnnotationName = nameKept,
            ObjectIfMatch = currentEtag,
        });
        await ExpectError(
            () => S3Fidelity.GetAnnotation(s3, bucket, key, nameKept),
            "NoSuchAnnotation", "The specified annotation does not exist.");
    }

    /// <summary>
    /// <c>expect_error(fn, code, message)</c>: <paramref name="call"/> fails with exactly this code and
    /// message; an unexpected success fails the check.
    /// </summary>
    private static async Task ExpectError(Func<Task> call, string code, string message)
    {
        var error = await FidelityContext.CaptureError(call)
            ?? throw Fail($"expected ({Py.Repr(code)}, {Py.Repr(message)}) but the call succeeded");
        Ensure(error.Code == code, $"expected code {Py.Repr(code)}, got {Py.Repr(error.Code)}");
        Ensure(error.Message == message, $"expected message {Py.Repr(message)}, got {Py.Repr(error.Message)}");
    }
}
