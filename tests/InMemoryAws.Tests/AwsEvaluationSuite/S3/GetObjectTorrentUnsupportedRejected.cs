// Translated from behavioral/s3/check_get_object_torrent_unsupported_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.get-object-torrent-unsupported-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetObjectTorrentUnsupportedRejected
{
    /// <summary>
    /// GetObjectTorrent on a regular object must error (BitTorrent distribution is unsupported /
    /// discontinued), not fabricate a torrent payload.
    /// </summary>
    [Test]
    [DisplayName("s3.get-object-torrent-unsupported-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-s3-");
        var s3 = ctx.S3;
        const string key = "torrent-target.bin";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "payload-for-torrent-check" });

        string? code = null;
        string? message = null;
        var bodyReturned = false;
        var (resp, error) = await FidelityContext.Capture(() => s3.GetObjectTorrentAsync(new GetObjectTorrentRequest { BucketName = bucket, Key = key }));
        if (resp is not null)
        {
            // If the call didn't raise, make sure we don't silently treat a bogus
            // body as acceptable -- drain it so we can report what happened.
            using (resp)
            {
                using var memory = new MemoryStream();
                await resp.ResponseStream.CopyToAsync(memory, cancellationToken);
                bodyReturned = memory.Length > 0;
            }
        }
        else
        {
            code = error!.Code;
            message = error.Message;
        }

        Ensure(
            code is not null,
            $"GetObjectTorrent unexpectedly succeeded and returned a torrent body (body_returned={(bodyReturned ? "True" : "False")}); real AWS must reject this operation");
        Ensure(code == "MethodNotAllowed", $"expected MethodNotAllowed, got {Py.Repr(code)}");
        Ensure(
            message == "The specified method is not allowed against this resource.",
            $"expected the exact MethodNotAllowed message, got {Py.Repr(message)}");

        // Independent read through a different operation: the rejected call must have
        // had no side effect on the object it targeted.
        var readback = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(
            readback.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes("payload-for-torrent-check")),
            "object content changed after a rejected GetObjectTorrent call");
    }
}
