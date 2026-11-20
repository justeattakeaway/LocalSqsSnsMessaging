// Translated from behavioral/s3/check_rename_object_source_if_none_match_precondition_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.rename-object-source-if-none-match-precondition-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class RenameObjectSourceIfNoneMatchPreconditionRejected
{
    /// <summary>
    /// RenameObject with SourceIfNoneMatch='*' fails with PreconditionFailed. The source object stays
    /// intact and the destination key is not created.
    /// </summary>
    [Test]
    [DisplayName("s3.rename-object-source-if-none-match-precondition-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var (bucket, _) = await S3Fidelity.CreateDirectoryBucket(ctx, s3, FidelityContext.UniqueName("sim2realdb"), S3Fidelity.ExpressZoneIds[ctx.Region]);

        // Positive control: an unconditioned RenameObject on this bucket/session actually succeeds,
        // so the PreconditionFailed asserted below is attributable to SourceIfNoneMatch, not to
        // RenameObject being broken or unsupported here.
        const string ctrlSrcKey = "control-src.txt";
        const string ctrlDstKey = "control-dst.txt";
        var ctrlBody = "control-body"u8.ToArray();
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = ctrlSrcKey, InputStream = new MemoryStream(ctrlBody) });
        await s3.RenameObjectAsync(new RenameObjectRequest { BucketName = bucket, Key = ctrlDstKey, RenameSource = ctrlSrcKey });
        Ensure((await S3Fidelity.GetBytes(s3, bucket, ctrlDstKey)).SequenceEqual(ctrlBody), "positive control rename did not move the body");
        var ctrlSrcGone = await S3Fidelity.ErrorPair(() => s3.GetObjectAsync(bucket, ctrlSrcKey));
        Ensure(
            ctrlSrcGone == ("NoSuchKey", "The specified key does not exist."),
            $"positive control rename should have removed the source key, got {S3Fidelity.Repr(ctrlSrcGone)}");

        const string srcKey = "src-precondition.txt";
        const string dstKey = "dst-precondition.txt";
        var body = "do-not-rename-me"u8.ToArray();
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = srcKey, InputStream = new MemoryStream(body) });
        var refusal = await S3Fidelity.ErrorPair(() => s3.RenameObjectAsync(new RenameObjectRequest
        {
            BucketName = bucket,
            Key = dstKey,
            RenameSource = srcKey,
            SourceIfNoneMatch = "*",
        }));
        Ensure(
            refusal == ("PreconditionFailed", "At least one of the pre-conditions you specified did not hold"),
            $"expected PreconditionFailed, got {S3Fidelity.Repr(refusal)}");

        // source object must be untouched, destination must not have been created
        Ensure((await S3Fidelity.GetBytes(s3, bucket, srcKey)).SequenceEqual(body), "assert still_there['Body'].read() == body");
        var dstGone = await S3Fidelity.ErrorPair(() => s3.GetObjectAsync(bucket, dstKey));
        Ensure(dstGone == ("NoSuchKey", "The specified key does not exist."), $"destination should not exist, got {S3Fidelity.Repr(dstGone)}");
    }
}
