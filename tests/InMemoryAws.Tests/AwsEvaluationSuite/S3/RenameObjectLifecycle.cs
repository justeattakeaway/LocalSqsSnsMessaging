// Translated from behavioral/s3/check_rename_object_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.rename-object-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class RenameObjectLifecycle
{
    /// <summary>
    /// RenameObject moves an object in a directory bucket: new key becomes readable, old key stops
    /// resolving.
    /// </summary>
    [Test]
    [DisplayName("s3.rename-object-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.NewDirectoryBucket(ctx, s3);
        const string src = "rename-src.txt";
        const string dst = "rename-dst.txt";
        var body = "rename-me"u8.ToArray();
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = name, Key = src, InputStream = new MemoryStream(body) });
        await s3.RenameObjectAsync(new RenameObjectRequest { BucketName = name, Key = dst, RenameSource = src });

        var moved = await s3.GetObjectMetadataAsync(name, dst);
        Ensure(moved.ContentLength == body.Length, $"renamed object length mismatch: expected {body.Length}, got {moved.ContentLength}");
        var gotBody = await S3Fidelity.GetBytes(s3, name, dst);
        Ensure(gotBody.SequenceEqual(body), $"renamed object content mismatch: expected {Py.Repr(body)}, got {Py.Repr(gotBody)}");
        var gone = await S3Fidelity.ErrorPair(() => s3.GetObjectAsync(name, src));
        Ensure(
            gone == ("NoSuchKey", "The specified key does not exist."),
            $"old key must stop resolving after rename, GetObject gave {S3Fidelity.Repr(gone)}");
    }
}
