// Translated from behavioral/s3/check_object_torrent_retrieval.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-torrent-retrieval</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectTorrentRetrieval
{
    /// <summary>
    /// GetObjectTorrent (BitTorrent delivery) has been retired: S3 now rejects every request for it with
    /// MethodNotAllowed, even for a real, freshly-stored object.
    /// </summary>
    [Test]
    [DisplayName("s3.object-torrent-retrieval")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-s3-behav-");
        const string key = "media/sample.bin";
        var source = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("torrent-source-bytes", 100)));
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(source) });

        var error = await FidelityContext.CaptureError(async () =>
        {
            using var torrent = await s3.GetObjectTorrentAsync(new GetObjectTorrentRequest { BucketName = bucket, Key = key });
        }) ?? throw Fail("expected get_object_torrent to raise, but it returned a body");
        Ensure(error.Code == "MethodNotAllowed", $"expected MethodNotAllowed (BitTorrent delivery retired), got {error.Code}");
        Ensure(
            error.Message == "The specified method is not allowed against this resource.",
            $"expected the exact MethodNotAllowed message, got {Py.Repr(error.Message)}");

        // The rejected call had no side effect: the object itself is still retrievable untouched.
        var body = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(
            body.AsSpan().SequenceEqual(source),
            $"expected the rejected GetObjectTorrent to leave the object untouched, got {body.Length} bytes back");
    }
}
