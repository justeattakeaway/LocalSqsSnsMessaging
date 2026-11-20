// Translated from behavioral/s3/check_get_object_torrent.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.get-object-torrent</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetObjectTorrent
{
    /// <summary>
    /// S3 has withdrawn BitTorrent delivery: GetObjectTorrent on a small unencrypted object fails
    /// with MethodNotAllowed, while plain GetObject works and the object is unchanged.
    /// </summary>
    [Test]
    [DisplayName("s3.get-object-torrent")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "torrent-me.bin";
        var body = new byte[4096];
        Array.Fill(body, (byte)'T');
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = name, Key = key, InputStream = new MemoryStream(body) });

        // Positive control: plain reads of the same object work.
        Ensure((await S3Fidelity.GetBytes(s3, name, key)).AsSpan().SequenceEqual(body), "positive control: plain GetObject must return what PutObject wrote");

        var (resp, exc) = await FidelityContext.Capture(() => s3.GetObjectTorrentAsync(new GetObjectTorrentRequest { BucketName = name, Key = key }));
        if (resp is not null)
        {
            using var memory = new MemoryStream();
            if (resp.ResponseStream is { } raw)
            {
                await raw.CopyToAsync(memory, cancellationToken);
            }

            resp.Dispose();
            throw Fail(
                $"GetObjectTorrent unexpectedly succeeded and returned {memory.Length} bytes; "
                + "real AWS has withdrawn BitTorrent delivery and must reject this call");
        }

        Ensure(exc!.Code == "MethodNotAllowed", $"expected MethodNotAllowed (BitTorrent delivery retired), got {Py.Repr(exc.Code)}: {exc}");
        Ensure(
            exc.Message == "The specified method is not allowed against this resource.",
            $"expected the exact MethodNotAllowed message, got {Py.Repr(exc.Message)}");

        // Effect-absent: the rejected call must not have touched the object itself.
        Ensure((await S3Fidelity.GetBytes(s3, name, key)).AsSpan().SequenceEqual(body), "object must be unchanged after the rejected GetObjectTorrent call");
    }
}
