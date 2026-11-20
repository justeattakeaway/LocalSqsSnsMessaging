// Translated from behavioral/s3/check_rename_object_state_transition.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.rename-object-state-transition</summary>
[Category("AwsEvaluationSuite")]
public sealed class RenameObjectStateTransition
{
    /// <summary>RenameObject atomically moves an object: old key -> 404, new key exposes identical content/metadata.</summary>
    [Test]
    [DisplayName("s3.rename-object-state-transition")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var (bucket, _) = await S3Fidelity.CreateDirectoryBucket(ctx, s3, FidelityContext.UniqueName("sim2realdb"), S3Fidelity.ExpressZoneIds[ctx.Region]);
        const string srcKey = "src-object.txt";
        const string dstKey = "dst-object.txt";
        var body = "rename-me-content"u8.ToArray();
        var put = new PutObjectRequest { BucketName = bucket, Key = srcKey, InputStream = new MemoryStream(body) };
        put.Metadata.Add("purpose", "rename-test");
        await s3.PutObjectAsync(put);
        await s3.RenameObjectAsync(new RenameObjectRequest { BucketName = bucket, Key = dstKey, RenameSource = srcKey });

        var gone = await S3Fidelity.ErrorPair(() => s3.GetObjectAsync(bucket, srcKey));
        Ensure(gone == ("NoSuchKey", "The specified key does not exist."), $"expected source key removed (NoSuchKey), got {S3Fidelity.Repr(gone)}");
        using var got = await s3.GetObjectAsync(bucket, dstKey);
        var actualBody = await S3Fidelity.ReadBody(got);
        Ensure(actualBody.SequenceEqual(body), $"expected renamed object body {Py.Repr(body)}, got {Py.Repr(actualBody)}");
        var actualMeta = S3Fidelity.UserMetadata(got.Metadata).GetValueOrDefault("purpose");
        Ensure(actualMeta == "rename-test", $"expected metadata purpose='rename-test' preserved after rename, got {Py.Repr(actualMeta)}");
    }
}
