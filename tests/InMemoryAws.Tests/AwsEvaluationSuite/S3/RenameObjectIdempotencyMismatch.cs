// Translated from behavioral/s3/check_rename_object_idempotency_mismatch.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.rename-object-idempotency-mismatch</summary>
[Category("AwsEvaluationSuite")]
public sealed class RenameObjectIdempotencyMismatch
{
    private const string MismatchMessage =
        "Parameters on this idempotent request are inconsistent with parameters used in previous request(s).";

    /// <summary>
    /// RenameObject on a directory bucket replayed with the same ClientToken and parameters succeeds as
    /// a no-op, while the same token with a different source/destination fails with
    /// IdempotentParameterMismatch and its exact message and renames nothing.
    /// </summary>
    [Test]
    [DisplayName("s3.rename-object-idempotency-mismatch")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var baseName = FidelityContext.UniqueName("sim2real").TrimEnd('-').Replace("-", string.Empty, StringComparison.Ordinal)[..20];
        var (bucket, _) = await S3Fidelity.CreateDirectoryBucket(ctx, s3, baseName, S3Fidelity.AvailabilityZoneIds(ctx));
        const string srcKey = "src-object.txt";
        const string dstKeyA = "dst-a.txt";
        const string srcKey2 = "src-object-2.txt";
        const string dstKeyB = "dst-b.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = srcKey, ContentBody = "rename-me" });
        var token = FidelityContext.UniqueName("tok-");
        await s3.RenameObjectAsync(new RenameObjectRequest { BucketName = bucket, Key = dstKeyA, RenameSource = srcKey, ClientToken = token });

        // Confirm the rename actually happened: independent read at the destination.
        await s3.GetObjectMetadataAsync(bucket, dstKeyA);

        // Retry with the SAME client token and the IDENTICAL parameters: idempotent
        // no-op success, even though src_key was already consumed by the rename above.
        var resp = await s3.RenameObjectAsync(new RenameObjectRequest { BucketName = bucket, Key = dstKeyA, RenameSource = srcKey, ClientToken = token });
        Ensure(
            (int)resp.HttpStatusCode == 200,
            $"expected the identical-params replay to be an idempotent no-op success, got HTTP {(int)resp.HttpStatusCode}");

        // Retry with the SAME client token but DIFFERENT parameters (a different, still-
        // existing source and a different destination Key): must be rejected as a
        // parameter mismatch on the idempotent request, not executed. A fresh source is
        // used here specifically so the rejection cannot be confused with "source gone".
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = srcKey2, ContentBody = "do-not-rename-me" });
        var refusal = await S3Fidelity.ErrorPair(
            () => s3.RenameObjectAsync(new RenameObjectRequest { BucketName = bucket, Key = dstKeyB, RenameSource = srcKey2, ClientToken = token }),
            "expected a ClientError but the call succeeded");
        Ensure(refusal.Message == MismatchMessage, $"unexpected message {Py.Repr(refusal.Message)}");
        Ensure(
            refusal.Code == "IdempotentParameterMismatch",
            $"expected IdempotentParameterMismatch reusing a client token with different params, got {S3Fidelity.Repr(refusal)}");

        // The forbidden rename must not have happened: destination absent, source untouched.
        var absent = await S3Fidelity.ErrorPair(() => s3.GetObjectAsync(bucket, dstKeyB), "expected a ClientError but the call succeeded");
        Ensure(absent.Message == "The specified key does not exist.", $"unexpected message {Py.Repr(absent.Message)}");
        Ensure(
            absent.Code == "NoSuchKey",
            $"mismatched idempotent retry must not perform the rename; GetObject on {Py.Repr(dstKeyB)} gave {S3Fidelity.Repr(absent)}");
        await s3.GetObjectMetadataAsync(bucket, srcKey2); // still present, unrenamed
    }
}
