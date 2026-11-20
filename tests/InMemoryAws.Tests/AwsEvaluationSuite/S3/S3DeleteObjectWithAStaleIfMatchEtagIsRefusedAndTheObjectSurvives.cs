// Translated from behavioral/s3/check_s3_delete_object_with_a_stale_if_match_etag_is_refused_and_the_object_survives.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-delete-object-with-a-stale-if-match-etag-is-refused-and-the-object-survives</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3DeleteObjectWithAStaleIfMatchEtagIsRefusedAndTheObjectSurvives
{
    private static readonly byte[] V1 = Encoding.ASCII.GetBytes("version-one-bytes-that-must-not-be-what-survives");
    private static readonly byte[] V2 = Encoding.ASCII.GetBytes("version-two-bytes-and-these-are-the-current-content-of-the-key");

    /// <summary>
    /// A DeleteObject whose IfMatch is the overwritten version's ETag fails 412 and leaves the
    /// current bytes; the same delete with the current ETag succeeds as the positive control.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-delete-object-with-a-stale-if-match-etag-is-refused-and-the-object-survives")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "conditionally-deleted.bin";

        var v1Etag = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(V1) })).ETag;
        var put2 = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(V2) });
        var v2Etag = put2.ETag;
        Ensure(
            v1Etag != v2Etag,
            $"the overwrite produced the same ETag {Py.Repr(v2Etag)} for different bytes, so this check cannot distinguish a stale ETag from a current one");

        // --- Break exactly one thing: hand the delete the ETag of the version it replaced.
        var exc = await FidelityContext.CaptureError(() => s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, IfMatch = v1Etag }));
        Ensure(
            exc is not null,
            $"DeleteObject with the stale ETag {Py.Repr(v1Etag)} succeeded on a key whose current ETag is {Py.Repr(v2Etag)} — IfMatch is being ignored, so every optimistic-concurrency delete in this emulator silently destroys a version the caller never saw");
        Ensure(exc!.Code == "PreconditionFailed", $"expected PreconditionFailed for a stale IfMatch on a delete, got {Py.Repr(exc.Code)}");
        Ensure(
            exc.Message == "At least one of the pre-conditions you specified did not hold",
            $"unexpected PreconditionFailed message for a stale IfMatch on a delete: {Py.Repr(exc.Message)}");
        Ensure((int)exc.StatusCode == 412, $"expected HTTP 412 for PreconditionFailed, got {(int)exc.StatusCode}");

        // --- Effect absent: the object is untouched, by bytes and by ETag.
        var body = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(
            body.AsSpan().SequenceEqual(V2),
            $"the refused conditional delete changed the object: it now holds {Py.Repr(Encoding.ASCII.GetString(body))}, want {Py.Repr(Encoding.ASCII.GetString(V2))}");
        var head = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(head.ETag == v2Etag, $"the object's ETag is now {Py.Repr(head.ETag)}, want the untouched {Py.Repr(v2Etag)}");
        Ensure(head.ContentLength == V2.Length, $"the object is {head.ContentLength} bytes, want {V2.Length}");
        var listed = await S3CoreFidelity.ListAllKeys(s3, bucket);
        Ensure(listed.SequenceEqual([key]), $"after the refused delete the bucket lists {Py.Repr(listed)}, want exactly [{Py.Repr(key)}]");

        // --- POSITIVE CONTROL: the identical call with the CURRENT ETag must delete the key.
        var removed = await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, IfMatch = v2Etag });
        Ensure(
            removed.HttpStatusCode == HttpStatusCode.NoContent,
            $"the conditional delete with the current ETag returned {(int)removed.HttpStatusCode}, expected 204");
        var gone = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, key));
        Ensure(gone is not null, "the conditional delete with the current ETag reported success but the key is still there");
        Ensure(
            (int)gone!.StatusCode == 404,
            $"HeadObject after the successful conditional delete returned {(int)gone.StatusCode}, expected 404");
        var empty = await S3CoreFidelity.ListAllKeys(s3, bucket);
        Ensure(empty.Count == 0, $"the bucket still lists {Py.Repr(empty)} after the successful delete");
    }
}
