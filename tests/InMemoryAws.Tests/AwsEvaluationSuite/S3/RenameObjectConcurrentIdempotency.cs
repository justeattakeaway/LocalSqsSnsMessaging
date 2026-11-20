// Translated from behavioral/s3/check_rename_object_concurrent_idempotency.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.rename-object-concurrent-idempotency</summary>
[Category("AwsEvaluationSuite")]
public sealed class RenameObjectConcurrentIdempotency
{
    /// <summary>
    /// Two concurrent RenameObject calls sharing a ClientToken rename the object once: a racer that does
    /// not succeed gets NoSuchKey with the exact message, and the same token with other parameters is
    /// refused with IdempotentParameterMismatch.
    /// </summary>
    [Test]
    [DisplayName("s3.rename-object-concurrent-idempotency")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var baseName = new string(FidelityContext.UniqueName("sim2exp").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray())[..19];
        var (bucket, _) = await S3Fidelity.CreateDirectoryBucket(ctx, s3, baseName, S3Fidelity.AvailabilityZoneIds(ctx));
        const string srcKey = "rename-src";
        const string dstKey = "rename-dst";
        var payload = "payload-for-idempotent-rename"u8.ToArray();
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = srcKey, InputStream = new MemoryStream(payload) });
        var token = FidelityContext.UniqueName("tok-");

        var results = await S3CoreFidelity.RunConcurrent(ctx, Enumerable.Range(0, 2).Select<int, Func<Task<RenameObjectResponse>>>(
            _ => () => s3.RenameObjectAsync(new RenameObjectRequest { BucketName = bucket, Key = dstKey, RenameSource = srcKey, ClientToken = token })).ToList());
        var oks = results.Where(r => r is RenameObjectResponse).ToList();
        var errs = results.OfType<AmazonS3Exception>().Select(e => (AwsError.From(e).Code, AwsError.From(e).Message)).ToList();
        var others = results.Where(r => r is Exception and not AmazonS3Exception).ToList();
        Ensure(others.Count == 0, $"unexpected failures: {string.Join("; ", others)}");

        // A racer that runs after the winner finds the source gone or replays the token's result.
        Ensure(oks.Count > 0, $"expected at least one RenameObject racer to succeed, got: {Py.Repr(errs.Select(e => $"{e.Code}: {e.Message}").ToList())}");
        Ensure(
            errs.All(err => err == ("NoSuchKey", "The specified key does not exist.")),
            $"a losing racer must fail only with NoSuchKey and its exact message, got {Py.Repr(errs.Select(e => $"{e.Code}: {e.Message}").ToList())}");

        // Convergence: exactly one rename took effect -- destination present, source gone.
        var dstHead = await s3.GetObjectMetadataAsync(bucket, dstKey);
        Ensure(dstHead.ContentLength == payload.Length, "assert dst_head['ContentLength'] == len(payload)");
        var srcError = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, srcKey));
        var srcPair = srcError is null ? ((string?, string?)?)null : S3Fidelity.Botocore(srcError);
        Ensure(
            srcPair == ("404", "Not Found"),
            $"expected rename source to be gone after rename, got head_object error {(srcPair is { } p ? S3Fidelity.Repr(p) : "None")}");

        // Reusing the SAME client token with DIFFERENT parameters must be rejected, not
        // silently treated as a fresh rename.
        const string otherSrc = "rename-src-2";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = otherSrc, ContentBody = "other-payload" });
        var mismatch = await FidelityContext.CaptureError(
            () => s3.RenameObjectAsync(new RenameObjectRequest { BucketName = bucket, Key = "rename-dst-2", RenameSource = otherSrc, ClientToken = token }));
        Ensure(
            mismatch?.Code == "IdempotentParameterMismatch",
            $"expected IdempotentParameterMismatch on token reuse with different params, got {Py.Repr(mismatch?.Code)}");

        // The mismatched retry must not have performed a second rename.
        var otherStillPresent = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, otherSrc)) is null;
        Ensure(otherStillPresent, "IdempotentParameterMismatch must not perform the rename");
    }
}
