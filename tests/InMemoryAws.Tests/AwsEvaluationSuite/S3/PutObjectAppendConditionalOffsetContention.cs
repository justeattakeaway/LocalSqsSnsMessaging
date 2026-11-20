// Translated from behavioral/s3/check_put_object_append_conditional_offset_contention.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-object-append-conditional-offset-contention</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutObjectAppendConditionalOffsetContention
{
    /// <summary>
    /// Two concurrent append-mode PutObject calls that both target the same stale write offset must
    /// resolve to exactly one success and one InvalidWriteOffset rejection -- never both succeeding
    /// (which would silently corrupt/duplicate data).
    /// </summary>
    [Test]
    [DisplayName("s3.put-object-append-conditional-offset-contention")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var baseName = new string(FidelityContext.UniqueName("sim2exp").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray())[..19];
        var (bucket, _) = await S3Fidelity.CreateDirectoryBucket(ctx, s3, baseName, S3Fidelity.AvailabilityZoneIds(ctx));
        const string key = "append-object";
        var initial = S3Fidelity.Repeat('A', 4096);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(initial) });
        var offset = (await s3.GetObjectMetadataAsync(bucket, key)).ContentLength;
        Ensure(
            offset == initial.Length,
            $"expected the freshly-written object to be {initial.Length} bytes, HeadObject reports {offset}");

        var chunkA = S3Fidelity.Repeat('B', 2048);
        var chunkB = S3Fidelity.Repeat('C', 2048);
        var results = await S3CoreFidelity.RunConcurrent(ctx, new[] { chunkA, chunkB }
            .Select<byte[], Func<Task<PutObjectResponse>>>(body => () => S3Fidelity.Append(s3, bucket, key, body, offset)).ToList());
        var successes = results.OfType<PutObjectResponse>().ToList();
        var failures = results.OfType<AmazonS3Exception>().Select(AwsError.From).ToList();
        var summary = string.Join("; ", results.Select(r => r is AmazonS3Exception e ? $"err {e.ErrorCode}" : r is Exception x ? $"exception {x.GetType().Name}" : "ok"));
        Ensure(successes.Count == 1, $"expected exactly one successful append, got: {summary}");
        Ensure(failures.Count == 1, $"expected exactly one rejected append, got: {summary}");
        Ensure(failures[0].Code == "InvalidWriteOffset", $"expected InvalidWriteOffset for the losing racer, got {Py.Repr(failures[0].Code)}");
        const string want = "The write offset value that you specified does not match the current object size.";
        Ensure(failures[0].Message == want, $"expected {Py.Repr(want)} for the losing racer, got {Py.Repr(failures[0].Message)}");

        var final = await s3.GetObjectMetadataAsync(bucket, key);
        var expectedSize = offset + chunkA.Length; // exactly one chunk landed, never both
        Ensure(
            final.ContentLength == expectedSize,
            $"expected final object size {expectedSize} (one append applied), got {final.ContentLength} (results={summary})");
    }
}
