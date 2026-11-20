// Translated from behavioral/s3/check_copy_object_source_if_match_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.copy-object-source-if-match-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CopyObjectSourceIfMatchMismatchRejected
{
    private static readonly (string? Code, string? Message) PreconditionFailed = ("PreconditionFailed", "At least one of the pre-conditions you specified did not hold");
    private static readonly (string? Code, string? Message) HeadAbsent = ("404", "Not Found");

    /// <summary>
    /// CopyObject with a CopySourceIfMatch that differs from the source's current ETag is refused with
    /// PreconditionFailed and its exact message and creates no destination; naming the current ETag
    /// copies the source byte for byte.
    /// </summary>
    [Test]
    [DisplayName("s3.copy-object-source-if-match-mismatch-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var srcBody = Encoding.ASCII.GetBytes("source-body");
        var sourceEtag = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "cond-src.txt", InputStream = new MemoryStream(srcBody) })).ETag;

        var refusal = await S3Fidelity.ErrorPair(() => s3.CopyObjectAsync(new CopyObjectRequest
        {
            DestinationBucket = bucket,
            DestinationKey = "cond-dst.txt",
            SourceBucket = bucket,
            SourceKey = "cond-src.txt",
            ETagToMatch = "\"00000000000000000000000000000000\"",
        }));
        Ensure(refusal == PreconditionFailed, $"expected {PreconditionFailed}, got {refusal}");

        var absent = await S3Fidelity.HeadErrorPair(() => s3.GetObjectMetadataAsync(bucket, "cond-dst.txt"));
        Ensure(
            absent == HeadAbsent,
            $"a failed conditional copy must not create the destination object: HeadObject gave {absent}, want {HeadAbsent}");

        // --- POSITIVE CONTROL: the identical call with the source's CURRENT ETag must land the copy.
        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            DestinationBucket = bucket,
            DestinationKey = "cond-dst.txt",
            SourceBucket = bucket,
            SourceKey = "cond-src.txt",
            ETagToMatch = sourceEtag,
        });
        var dstBody = await S3Fidelity.GetBytes(s3, bucket, "cond-dst.txt");
        Ensure(
            dstBody.AsSpan().SequenceEqual(srcBody),
            $"the correctly-conditioned copy landed {Py.Repr(dstBody)}, want the source bytes {Py.Repr(srcBody)}");
    }
}
