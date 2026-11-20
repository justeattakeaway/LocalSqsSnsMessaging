// Translated from behavioral/s3/check_copy_object_same_bucket.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.copy-object-same-bucket</summary>
[Category("AwsEvaluationSuite")]
public sealed class CopyObjectSameBucket
{
    private static readonly (string? Code, string? Message) HeadAbsent = ("404", "Not Found");

    /// <summary>CopyObject within the same bucket produces a byte-identical, independent copy.</summary>
    [Test]
    [DisplayName("s3.copy-object-same-bucket")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        var body = Encoding.ASCII.GetBytes("copy-source-data");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "src.txt", InputStream = new MemoryStream(body) });
        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            DestinationBucket = bucket,
            DestinationKey = "dst.txt",
            SourceBucket = bucket,
            SourceKey = "src.txt",
        });
        var got = await S3Fidelity.GetBytes(s3, bucket, "dst.txt");
        Ensure(got.AsSpan().SequenceEqual(body), $"copy destination bytes {Py.Repr(got)} did not match source bytes {Py.Repr(body)}");

        // the copy must be independent of the source: deleting dst.txt must not touch src.txt,
        // and dst.txt itself must actually be gone (not aliased back to src.txt)
        await s3.DeleteObjectAsync(bucket, "dst.txt");
        var got404 = S3Fidelity.HeadError(
            await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, "dst.txt"))
            ?? throw Fail("dst.txt was still readable after delete_object"));
        Ensure(got404 == HeadAbsent, $"expected {HeadAbsent} for deleted dst.txt, got {got404}");
        var stillThere = await S3Fidelity.GetBytes(s3, bucket, "src.txt");
        Ensure(
            stillThere.AsSpan().SequenceEqual(body),
            $"deleting the copy destination changed the source object: expected {Py.Repr(body)}, got {Py.Repr(stillThere)}");
    }
}
