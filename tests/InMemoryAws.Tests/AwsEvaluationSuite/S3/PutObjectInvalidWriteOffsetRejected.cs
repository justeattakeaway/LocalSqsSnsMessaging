// Translated from behavioral/s3/check_put_object_invalid_write_offset_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-object-invalid-write-offset-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutObjectInvalidWriteOffsetRejected
{
    /// <summary>
    /// PutObject append with an out-of-order x-amz-write-offset-bytes on a directory-bucket appendable
    /// object returns InvalidWriteOffset and leaves the object unmodified; a correctly-offset append
    /// succeeds and the bytes are appended in order.
    /// </summary>
    [Test]
    [DisplayName("s3.put-object-invalid-write-offset-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var (bucket, _) = await S3Fidelity.CreateDirectoryBucket(ctx, s3, FidelityContext.UniqueName("sim2realdb"), S3Fidelity.ExpressZoneIds[ctx.Region]);
        var key = FidelityContext.UniqueName("append-obj") + ".log";
        var initialBody = "0123456789"u8.ToArray(); // 10 bytes
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(initialBody) });

        // Positive control: append at the correct current object size succeeds.
        var goodChunk = "ABCDE"u8.ToArray();
        await S3Fidelity.Append(s3, bucket, key, goodChunk, initialBody.Length);
        var grown = await s3.GetObjectMetadataAsync(bucket, key);
        var expectedGrownSize = initialBody.Length + goodChunk.Length;
        Ensure(
            grown.ContentLength == expectedGrownSize,
            $"expected ContentLength {expectedGrownSize} after a correctly-offset append, got {grown.ContentLength}");
        var bodyAfterGoodAppend = await S3Fidelity.GetBytes(s3, bucket, key);
        byte[] expected = [.. initialBody, .. goodChunk];
        Ensure(
            bodyAfterGoodAppend.SequenceEqual(expected),
            $"expected the append to be placed at offset {initialBody.Length}, got body {Py.Repr(bodyAfterGoodAppend)}");

        // Negative: append with a bogus (too-small) offset must be rejected as InvalidWriteOffset.
        var currentSize = initialBody.Length + goodChunk.Length;
        var badOffset = currentSize - 1; // does not match the object's actual current size
        var code = await S3Fidelity.ErrorPair(
            () => S3Fidelity.Append(s3, bucket, key, "WRONG"u8.ToArray(), badOffset),
            "expected a rejection, but the call SUCCEEDED");
        Ensure(
            code == ("InvalidWriteOffset", "The write offset value that you specified does not match the current object size."),
            $"expected InvalidWriteOffset, got {S3Fidelity.Repr(code)}");

        // The rejected append must not have mutated the object.
        var unchanged = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(
            unchanged.ContentLength == currentSize,
            $"rejected append with bad offset {badOffset} must not mutate the object: expected ContentLength {currentSize}, got {unchanged.ContentLength}");
        var unchangedBody = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(
            unchangedBody.SequenceEqual(expected),
            $"rejected append with bad offset {badOffset} must not mutate the object body, got {Py.Repr(unchangedBody)}");
    }
}
