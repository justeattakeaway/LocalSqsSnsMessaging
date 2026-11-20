// Translated from behavioral/s3/check_put_object_append_write_offset_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-object-append-write-offset-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutObjectAppendWriteOffsetRejected
{
    /// <summary>
    /// On an S3 Express One Zone directory bucket, an append PutObject whose WriteOffsetBytes differs from
    /// the object size is rejected with InvalidWriteOffset and its exact message and leaves the content
    /// unchanged, while a correct-offset append grows it.
    /// </summary>
    [Test]
    [DisplayName("s3.put-object-append-write-offset-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewDirectoryBucket(ctx, s3);
        const string key = "wo-target.bin";
        var initial = "123456789"u8.ToArray(); // 9 bytes
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(initial) });

        // Positive control: appending at the CORRECT current offset must succeed and the bytes must
        // actually land in order. Without this, a target that rejects every WriteOffsetBytes append
        // (i.e. never implements append at all) would pass this check just as well as one that
        // implements append and correctly validates the offset.
        var goodTail = "ABCDE"u8.ToArray();
        await S3Fidelity.Append(s3, bucket, key, goodTail, initial.Length);
        byte[] grown = [.. initial, .. goodTail];
        Ensure((await S3Fidelity.GetBytes(s3, bucket, key)).SequenceEqual(grown), "a correctly-offset append must land its bytes in order");

        // Negative: an append whose WriteOffsetBytes does NOT equal the object's current size (now
        // len(grown)) must be rejected with InvalidWriteOffset, and must not mutate the object.
        var badOffset = grown.Length + 999;
        var code = await S3Fidelity.ErrorPair(
            () => S3Fidelity.Append(s3, bucket, key, "tail-bytes"u8.ToArray(), badOffset),
            "expected the call to be rejected, but it SUCCEEDED");
        Ensure(
            code == ("InvalidWriteOffset", "The write offset value that you specified does not match the current object size."),
            $"expected InvalidWriteOffset, got {S3Fidelity.Repr(code)}");
        Ensure((await S3Fidelity.GetBytes(s3, bucket, key)).SequenceEqual(grown), "object must be unmodified after a rejected append");
    }
}
