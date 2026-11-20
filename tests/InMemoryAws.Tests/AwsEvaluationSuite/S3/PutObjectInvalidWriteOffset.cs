// Translated from behavioral/s3/check_put_object_invalid_write_offset.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-object-invalid-write-offset</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutObjectInvalidWriteOffset
{
    /// <summary>
    /// PutObject append-mode (S3 Express One Zone directory buckets) with a WriteOffsetBytes value that
    /// does not match the object's current length returns InvalidWriteOffset instead of silently
    /// appending at the wrong position.
    /// </summary>
    [Test]
    [DisplayName("s3.put-object-invalid-write-offset")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var (bucket, _) = await S3Fidelity.CreateDirectoryBucket(ctx, s3, FidelityContext.UniqueName("sim2realdb"), S3Fidelity.ExpressZoneIds[ctx.Region]);
        const string key = "append-object.bin";
        var initial = "0123456789"u8.ToArray();
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(initial) });
        ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, key));
        var head = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(head.ContentLength == initial.Length, $"expected the initial object to be {initial.Length} bytes, got {head.ContentLength}");

        // Positive control: appending at the object's actual current length must succeed and the
        // bytes must land at the end, in order. Without this, "rejects a mismatched offset" is
        // indistinguishable from "rejects every WriteOffsetBytes append".
        var goodChunk = "ABCDE"u8.ToArray();
        await S3Fidelity.Append(s3, bucket, key, goodChunk, initial.Length);
        var grown = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(
            grown.ContentLength == initial.Length + goodChunk.Length,
            $"a correctly-offset append should grow the object to {initial.Length + goodChunk.Length} bytes, got {grown.ContentLength}");
        byte[] expected = [.. initial, .. goodChunk];
        Ensure((await S3Fidelity.GetBytes(s3, bucket, key)).SequenceEqual(expected), "a correctly-offset append must land its bytes at the end, in order");

        var currentSize = initial.Length + goodChunk.Length;
        var wrongOffset = currentSize + 999;
        var code = await S3Fidelity.ErrorPair(
            () => S3Fidelity.Append(s3, bucket, key, "tail-bytes"u8.ToArray(), wrongOffset),
            "expected a rejection, but the call SUCCEEDED");
        Ensure(
            code == ("InvalidWriteOffset", "The write offset value that you specified does not match the current object size."),
            $"expected InvalidWriteOffset appending at a mismatched offset, got {S3Fidelity.Repr(code)}");

        // Object must be unchanged after the rejected append.
        var headAfter = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(
            headAfter.ContentLength == currentSize,
            $"rejected append must not mutate the object's length; expected {currentSize}, got {headAfter.ContentLength}");
        Ensure((await S3Fidelity.GetBytes(s3, bucket, key)).SequenceEqual(expected), "rejected append must not mutate the object's bytes");

        // And a further legal append still works, so the rejection did not wedge the object.
        var finalChunk = "ZZ"u8.ToArray();
        await S3Fidelity.Append(s3, bucket, key, finalChunk, currentSize);
        var final = await S3Fidelity.GetBytes(s3, bucket, key);
        byte[] want = [.. expected, .. finalChunk];
        Ensure(final.SequenceEqual(want), $"a legal append after the rejection should produce {Py.Repr(want)}, got {Py.Repr(final)}");
    }
}
