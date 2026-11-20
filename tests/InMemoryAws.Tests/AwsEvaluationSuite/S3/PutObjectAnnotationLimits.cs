// Translated from behavioral/s3/check_put_object_annotation_limits.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-object-annotation-limits</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutObjectAnnotationLimits
{
    private const int MaxAnnotationsPerObject = 1000;
    private const int MaxAttempts = 20;

    /// <summary>
    /// PutObjectAnnotation rejects too-long, invalid-character and reserved-prefix names with exact
    /// errors, and on an object holding 1,000 annotations rejects one more with AnnotationLimitExceeded,
    /// evicting none; after one is deleted, a new one fits.
    /// </summary>
    [Test]
    [DisplayName("s3.put-object-annotation-limits")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");
        const string key = "annotated-object.bin";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "payload" });
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, key));

        // Name too long (> 512 bytes, otherwise made of valid characters).
        var codeTooLong = await Error(() => S3Fidelity.PutAnnotation(s3, bucket, key, new string('a', 513), "x"u8.ToArray()));
        Ensure(
            codeTooLong == ("AnnotationNameTooLong", "Your annotation name is too long."),
            $"expected AnnotationNameTooLong for a 513-byte name, got {S3Fidelity.Repr(codeTooLong)}");

        // Invalid character in the name. Real S3 rejects any structurally-invalid
        // annotation name (bad characters, reserved prefix, etc.) with the single
        // "InvalidAnnotation" code, not a name-specific "InvalidAnnotationName" code.
        var codeInvalidChar = await Error(() => S3Fidelity.PutAnnotation(s3, bucket, key, "bad!name", "x"u8.ToArray()));
        Ensure(
            codeInvalidChar == ("InvalidAnnotation", "Your annotation name contains invalid characters."),
            $"expected InvalidAnnotation for a name with disallowed characters, got {S3Fidelity.Repr(codeInvalidChar)}");

        // Reserved prefix.
        var codeReserved = await Error(() => S3Fidelity.PutAnnotation(s3, bucket, key, "aws-reserved", "x"u8.ToArray()));
        Ensure(
            codeReserved == ("InvalidAnnotation", "Your annotation name cannot start with 'aws' or 's3'."),
            $"expected InvalidAnnotation for a name starting with the reserved 'aws' prefix, got {S3Fidelity.Repr(codeReserved)}");

        // Fill the object up to the per-object annotation cap. Concurrent writes to one object's
        // annotation set draw SlowDown, bare 5xx/429 and contention errors, none of which is the cap
        // this check grades, so every fill write retries any ClientError with jittered backoff. The
        // cap itself is graded afterwards, sequentially, once the count has settled at 1,000.
        var results = await S3Fidelity.RunPool(ctx, 40, Enumerable.Range(0, MaxAnnotationsPerObject), async i =>
        {
            var name = $"ann-{i:0000}";
            string? lastCode = null;
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var error = await FidelityContext.CaptureError(() => S3Fidelity.PutAnnotation(s3, bucket, key, name, "1"u8.ToArray()));
                if (error is null)
                {
                    return null;
                }

                lastCode = error.Code;
                if (attempt == MaxAttempts - 1)
                {
                    // Last attempt already made; no point sleeping further before giving up.
                    break;
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(Math.Min(0.25 * Math.Pow(1.6, attempt), 3.0)));
            }

            return lastCode;
        });
        var failures = results.Where(r => r is not null).ToList();
        Ensure(failures.Count == 0, $"unexpected errors filling up to the annotation cap: {Py.Repr(failures.Take(5).ToList())}");

        // Poll briefly for the count to settle. AnnotationCount is page-scoped (like KeyCount), so the
        // page is sized to the cap and the returned annotations are counted.
        var actualCount = -1;
        var delay = 1.0;
        for (var i = 0; i < 10; i++)
        {
            var listing = await S3Fidelity.ListAnnotations(s3, bucket, key, max: MaxAnnotationsPerObject);
            actualCount = listing.Annotations?.Count ?? 0;
            if (actualCount == MaxAnnotationsPerObject && listing.NextContinuationToken is null)
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(delay));
            delay = Math.Min(delay * 1.5, 3.0);
        }

        Ensure(actualCount == MaxAnnotationsPerObject, $"expected {MaxAnnotationsPerObject} annotations, got {actualCount}");

        // One more annotation beyond the cap must be rejected.
        var codeOverCap = await Error(() => S3Fidelity.PutAnnotation(s3, bucket, key, "one-too-many", "1"u8.ToArray()));
        Ensure(
            codeOverCap == ("AnnotationLimitExceeded", $"The object annotation count ({MaxAnnotationsPerObject + 1}) exceeds the limit ({MaxAnnotationsPerObject})."),
            $"expected AnnotationLimitExceeded at the 1,001st annotation, got {S3Fidelity.Repr(codeOverCap)}");

        // The rejection must have had no effect: still exactly 1,000 annotations, the
        // rejected name absent, and every annotation the fill loop wrote still present
        // -- i.e. the cap rejects the write outright rather than silently evicting an
        // older annotation to make room, which is the claim this check exists to test.
        var afterReject = await S3Fidelity.ListAnnotations(s3, bucket, key, max: MaxAnnotationsPerObject);
        var namesAfterReject = S3Fidelity.AnnotationNames(afterReject).ToHashSet(StringComparer.Ordinal);
        Ensure(
            namesAfterReject.Count == MaxAnnotationsPerObject,
            $"expected {MaxAnnotationsPerObject} annotations after the rejected write, got {namesAfterReject.Count}");
        Ensure(!namesAfterReject.Contains("one-too-many"), "rejected annotation 'one-too-many' must not appear on the object");
        var expectedNames = Enumerable.Range(0, MaxAnnotationsPerObject).Select(i => $"ann-{i:0000}").ToHashSet(StringComparer.Ordinal);
        Ensure(namesAfterReject.SetEquals(expectedNames), "the rejected write must not have evicted an existing annotation to make room");

        // Restore: free exactly one slot, then the identical call must now succeed --
        // proving the prior rejection was the cap being enforced, not the object or the
        // operation being broken.
        await s3.DeleteObjectAnnotationAsync(new DeleteObjectAnnotationRequest { BucketName = bucket, Key = key, AnnotationName = "ann-0000" });
        var codeAfterFree = await Error(() => S3Fidelity.AnnotationPayload(s3, bucket, key, "ann-0000"));
        Ensure(
            codeAfterFree == ("NoSuchAnnotation", "The specified annotation does not exist."),
            $"expected NoSuchAnnotation for the deleted slot, got {S3Fidelity.Repr(codeAfterFree)}");
        await S3Fidelity.PutAnnotation(s3, bucket, key, "one-too-many", "1"u8.ToArray());
        var gotRestored = await S3Fidelity.AnnotationPayload(s3, bucket, key, "one-too-many");
        Ensure(gotRestored.SequenceEqual("1"u8.ToArray()), "expected 'one-too-many' to be readable back after the cap freed up a slot");
    }

    private static Task<(string? Code, string? Message)> Error(Func<Task> call) =>
        S3Fidelity.ErrorPair(call, "expected the call to be rejected, but it SUCCEEDED");
}
