// Translated from behavioral/s3/check_rename_object_idempotency_token.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.rename-object-idempotency-token</summary>
[Category("AwsEvaluationSuite")]
public sealed class RenameObjectIdempotencyToken
{
    private const string MismatchMessage =
        "Parameters on this idempotent request are inconsistent with parameters used in previous request(s).";

    /// <summary>
    /// Retrying RenameObject with the SAME ClientToken but a DIFFERENT request body (a different
    /// RenameSource) raises IdempotentParameterMismatch rather than silently replaying the first
    /// response or performing the new rename.
    /// </summary>
    [Test]
    [DisplayName("s3.rename-object-idempotency-token")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.NewDirectoryBucket(ctx, s3);
        const string src1 = "idem-src1.txt";
        const string src2 = "idem-src2.txt";
        const string dst = "idem-dst.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = name, Key = src1, ContentBody = "one" });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = name, Key = src2, ContentBody = "two" });
        var token = Guid.NewGuid().ToString();
        await s3.RenameObjectAsync(new RenameObjectRequest { BucketName = name, Key = dst, RenameSource = src1, ClientToken = token });

        // Positive control, via an independent read: the first rename actually happened.
        var dstBody = await S3Fidelity.GetBytes(s3, name, dst);
        Ensure(dstBody.SequenceEqual("one"u8.ToArray()), $"expected dst to hold src1's content after rename, got {Py.Repr(dstBody)}");
        var src1Gone = await S3Fidelity.ErrorPair(() => s3.GetObjectAsync(name, src1), "expected a ClientError but the call succeeded");
        Ensure(src1Gone.Message == "The specified key does not exist.", $"unexpected message {Py.Repr(src1Gone.Message)}");
        Ensure(src1Gone.Code == "NoSuchKey", $"expected src1 gone after rename, GetObject gave {S3Fidelity.Repr(src1Gone)}");

        var refusal = await S3Fidelity.ErrorPair(
            () => s3.RenameObjectAsync(new RenameObjectRequest { BucketName = name, Key = dst, RenameSource = src2, ClientToken = token }),
            "expected a ClientError but the call succeeded");
        Ensure(refusal.Message == MismatchMessage, $"unexpected message {Py.Repr(refusal.Message)}");
        Ensure(refusal.Code == "IdempotentParameterMismatch", $"expected IdempotentParameterMismatch, got {S3Fidelity.Repr(refusal)}");

        // Effect-absent, via an independent read: the mismatched retry must not have
        // performed the second rename.
        var dstBodyAfter = await S3Fidelity.GetBytes(s3, name, dst);
        Ensure(dstBodyAfter.SequenceEqual("one"u8.ToArray()), $"mismatched idempotent retry must not rename src2 over dst, got {Py.Repr(dstBodyAfter)}");
        var src2Body = await S3Fidelity.GetBytes(s3, name, src2);
        Ensure(src2Body.SequenceEqual("two"u8.ToArray()), $"src2 must remain untouched, got {Py.Repr(src2Body)}");
    }
}
