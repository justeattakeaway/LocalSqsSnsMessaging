// Translated from behavioral/s3/check_s3_concurrent_copy_while_the_source_is_being_deleted_never_yields_a_partial_object.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using Amazon.Runtime;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-concurrent-copy-while-the-source-is-being-deleted-never-yields-a-partial-object</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3ConcurrentCopyWhileTheSourceIsBeingDeletedNeverYieldsAPartialObject
{
    /// <summary>10 MiB — big enough that a non-atomic copy would be observable.</summary>
    private const int Size = 10 * 1024 * 1024;
    private const int Copies = 8;
    private const string SourceKey = "race-source.bin";

    /// <summary>
    /// Eight concurrent CopyObjects racing a source delete each end whole (source bytes and ETag)
    /// or as NoSuchKey with no destination; a copy after the delete is NoSuchKey and creates nothing.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-concurrent-copy-while-the-source-is-being-deleted-never-yields-a-partial-object")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        var sourceBytes = S3Fidelity.RandomBytes(Size);
        var sourceEtag = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = SourceKey, InputStream = new MemoryStream(sourceBytes) })).ETag;
        Ensure((await s3.GetObjectMetadataAsync(bucket, SourceKey)).ContentLength == Size, $"the source object is not {Size} bytes");

        async Task<(string Dest, string? ETag, (string?, string?)? Code)> Copy(int index)
        {
            var dest = $"race-dest-{index}.bin";
            try
            {
                var resp = await s3.CopyObjectAsync(new CopyObjectRequest { DestinationBucket = bucket, DestinationKey = dest, SourceBucket = bucket, SourceKey = SourceKey });
                return (dest, resp.ETag, null);
            }
            catch (AmazonServiceException ex)
            {
                var error = AwsError.From(ex);
                return (dest, null, (error.Code, error.Message));
            }
        }

        // How many copies win is scheduler-dependent, so only the per-destination outcome is asserted.
        (string Dest, string? ETag, (string?, string?)? Code)[] outcomes;
        using (ctx.Concurrently())
        {
            var copies = Enumerable.Range(0, Copies).Select(i => Task.Run(() => Copy(i))).ToList();
            var delete = Task.Run(() => s3.DeleteObjectAsync(bucket, SourceKey));
            outcomes = await Task.WhenAll(copies);
            await delete; // re-raise anything the delete thread hit
        }

        var succeeded = outcomes.Where(o => o.Code is null).ToList();
        var failed = outcomes.Where(o => o.Code is not null).ToList();
        Ensure(succeeded.Count + failed.Count == Copies, $"accounted for {succeeded.Count + failed.Count} outcomes, launched {Copies}");

        foreach (var (dest, etag, _) in succeeded)
        {
            Ensure(
                etag == sourceEtag,
                $"{dest} was copied with ETag {Py.Repr(etag)} but the source's is {Py.Repr(sourceEtag)} — a server-side copy of unchanged bytes must preserve the ETag, so this destination holds different content");
            var body = await S3Fidelity.GetBytes(s3, bucket, dest);
            Ensure(
                body.AsSpan().SequenceEqual(sourceBytes),
                $"{dest} holds {body.Length} bytes, the source held {Size} — a copy that raced the source's deletion produced a PARTIAL object, which CopyObject's atomicity forbids");
            var head = await s3.GetObjectMetadataAsync(bucket, dest);
            Ensure(head.ContentLength == Size, $"{dest} reports ContentLength {head.ContentLength}, want {Size}");
            Ensure(head.ETag == sourceEtag, $"{dest} reports ETag {Py.Repr(head.ETag)} on a HEAD, want {Py.Repr(sourceEtag)}");
        }

        foreach (var (dest, _, code) in failed)
        {
            Ensure(
                code == ("NoSuchKey", "The specified key does not exist."),
                $"{dest}'s copy failed with {code}; the only legal failure when the source is concurrently deleted is NoSuchKey");
            var absent = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, dest));
            Ensure(absent is not null, $"{dest}'s copy reported {code} but the destination object EXISTS — a failed copy must not leave a partial or empty object at the destination");
            Ensure(absent!.StatusCode == HttpStatusCode.NotFound, $"HeadObject on the failed destination {dest} returned {(int)absent.StatusCode}, expected 404");
        }

        var listed = (await S3Fidelity.ListedObjects(s3, bucket)).Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        Ensure(
            listed.SetEquals(succeeded.Select(s => s.Dest)),
            $"the bucket lists {Py.Repr(listed.Order(StringComparer.Ordinal).ToList())}; the successful copies were {Py.Repr(succeeded.Select(s => s.Dest).Order(StringComparer.Ordinal).ToList())} and the source was deleted — anything else is a destination that exists for a copy that failed, or a source that survived");

        // The delete often lands last and every copy succeeds; this deterministic copy from the
        // now-deleted source exercises the failure branch regardless.
        const string lateDest = "race-dest-after.bin";
        var late = await FidelityContext.CaptureError(() => s3.CopyObjectAsync(new CopyObjectRequest { DestinationBucket = bucket, DestinationKey = lateDest, SourceBucket = bucket, SourceKey = SourceKey }));
        Ensure(late is not null, $"CopyObject succeeded with {Py.Repr(SourceKey)} as its source after that key was deleted — the copy is reading stale state");
        Ensure(late!.Code == "NoSuchKey", $"expected NoSuchKey copying from a deleted source, got {Py.Repr(late.Code)}");
        Ensure(late.Message == "The specified key does not exist.", $"unexpected NoSuchKey message copying from a deleted source: {Py.Repr(late.Message)}");
        Ensure(late.StatusCode == HttpStatusCode.NotFound, $"expected HTTP 404, got {(int)late.StatusCode}");
        var lateAbsent = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, lateDest));
        Ensure(lateAbsent is not null, $"the refused copy created {Py.Repr(lateDest)} anyway");
        Ensure(lateAbsent!.StatusCode == HttpStatusCode.NotFound, $"HeadObject on {Py.Repr(lateDest)} returned {(int)lateAbsent.StatusCode}, expected 404");
        var stillListed = (await S3Fidelity.ListedObjects(s3, bucket)).Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        Ensure(stillListed.SetEquals(listed), $"the refused copy changed the bucket: {Py.Repr(stillListed.Order(StringComparer.Ordinal).ToList())} vs {Py.Repr(listed.Order(StringComparer.Ordinal).ToList())}");
    }
}
