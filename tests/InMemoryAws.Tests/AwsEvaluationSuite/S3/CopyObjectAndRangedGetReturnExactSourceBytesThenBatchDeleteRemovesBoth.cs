// Translated from behavioral/s3/check_copy_object_and_ranged_get_return_exact_source_bytes_then_batch_delete_removes_both.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.copy-object-and-ranged-get-return-exact-source-bytes-then-batch-delete-removes-both</summary>
[Category("AwsEvaluationSuite")]
public sealed class CopyObjectAndRangedGetReturnExactSourceBytesThenBatchDeleteRemovesBoth
{
    private static readonly (string, string) NoSuchKey = ("NoSuchKey", "The specified key does not exist.");
    private static readonly (string, string) InvalidRange = ("InvalidRange", "The requested range is not satisfiable");

    /// <summary>
    /// CopyObject of a random-length object keeps its MD5 ETag; ranged and suffix-ranged GETs of the
    /// copy return 206 with exactly the addressed bytes and Content-Range, a range at the size is refused
    /// 416 InvalidRange; DeleteObjects then removes source and copy so both read NoSuchKey.
    /// </summary>
    [Test]
    [DisplayName("s3.copy-object-and-ranged-get-return-exact-source-bytes-then-batch-delete-removes-both")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string src = "copy/source.bin";
        const string dst = "copy/nested/destination.bin";
        var size = 10000 + RandomNumberGenerator.GetInt32(20000);
        var body = RandomNumberGenerator.GetBytes(size);
        var etag = "\"" + Convert.ToHexStringLower(MD5.HashData(body)) + "\"";

        var putEtag = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = src, InputStream = new MemoryStream(body) })).ETag;
        Ensure(putEtag == etag, $"PutObject ETag {Py.Repr(putEtag)} != quoted MD5 {Py.Repr(etag)}");
        var copied = await s3.CopyObjectAsync(new CopyObjectRequest { SourceBucket = bucket, SourceKey = src, DestinationBucket = bucket, DestinationKey = dst });
        Ensure(copied.ETag == etag, $"CopyObject ETag {Py.Repr(copied.ETag)} != the source's quoted MD5 {Py.Repr(etag)}");
        using (var got = await s3.GetObjectAsync(bucket, dst))
        {
            var data = await S3Fidelity.ReadBody(got);
            Ensure(
                data.AsSpan().SequenceEqual(body) && got.ETag == etag,
                $"GetObject of the copy: {data.Length} bytes, ETag {Py.Repr(got.ETag)}; expected {size} bytes, {Py.Repr(etag)}");
        }

        var start = 1 + RandomNumberGenerator.GetInt32(size / 2);
        var end = start + RandomNumberGenerator.GetInt32(size - start - 1);
        var ranged = await Ranged(s3, bucket, dst, new ByteRange(start, end));
        Ensure(
            ranged.Equals((206, $"bytes {start}-{end}/{size}", end - start + 1, body[start..(end + 1)])),
            $"ranged GET bytes={start}-{end} of the {size}-byte copy did not return exactly that slice");
        var tail = 1 + RandomNumberGenerator.GetInt32(500);
        ranged = await Ranged(s3, bucket, dst, new ByteRange($"bytes=-{tail}"));
        Ensure(
            ranged.Equals((206, $"bytes {size - tail}-{size - 1}/{size}", tail, body[^tail..])),
            $"suffix GET bytes=-{tail} of the {size}-byte copy did not return exactly its last {tail} bytes");
        var refusal = Refusal(
            await Err(() => s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = dst, ByteRange = new ByteRange($"bytes={size}-") })),
            "GetObject range at the object size",
            416);
        Ensure(refusal == InvalidRange, $"GetObject bytes={size}-: expected {Py.Repr(InvalidRange)}, got {Py.Repr(refusal)}");

        var deleted = await s3.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = bucket, Objects = [new KeyVersion { Key = src }, new KeyVersion { Key = dst }] });
        var deletedKeys = (deleted.DeletedObjects ?? []).Select(d => d.Key).Order(StringComparer.Ordinal).ToList();
        Ensure(
            deletedKeys.SequenceEqual(new[] { src, dst }.Order(StringComparer.Ordinal)),
            $"DeleteObjects Deleted {Py.Repr(deletedKeys)}, expected both keys");
        Ensure((deleted.DeleteErrors ?? []).Count == 0, $"DeleteObjects reported errors {Py.Repr((deleted.DeleteErrors ?? []).Select(e => (e.Key, e.Code)).ToList())}");
        foreach (var key in new[] { src, dst })
        {
            refusal = Refusal(await Err(() => s3.GetObjectAsync(bucket, key)), $"GetObject {key} after delete", 404);
            Ensure(refusal == NoSuchKey, $"GetObject {key} after DeleteObjects: expected {Py.Repr(NoSuchKey)}, got {Py.Repr(refusal)}");
        }

        var left = (await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = "copy/" })).S3Objects ?? [];
        Ensure(left.Count == 0, $"keys still listed after DeleteObjects: {Py.Repr(left.Select(o => o.Key).ToList())}");
    }

    /// <summary>
    /// Call fn(); return the ClientError response it raises. An unexpected success raises, so a
    /// target that accepts what AWS refuses cannot read as a code mismatch.
    /// </summary>
    private static async Task<AwsError> Err(Func<Task> fn) =>
        await FidelityContext.CaptureError(fn) ?? throw Fail("expected a ClientError, but the call SUCCEEDED");

    /// <summary>(code, message) of a refusal, after grading its exact HTTP status.</summary>
    private static (string? Code, string? Message) Refusal(AwsError resp, string what, int status)
    {
        Ensure((int)resp.StatusCode == status, $"{what}: expected HTTP {status}, got {(int)resp.StatusCode}");
        return S3Fidelity.Botocore(resp);
    }

    /// <summary>(HTTP status, Content-Range, ContentLength, bytes) of a ranged GetObject.</summary>
    private static async Task<RangedRead> Ranged(IAmazonS3 s3, string bucket, string key, ByteRange spec)
    {
        using var got = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, ByteRange = spec });
        return new RangedRead((int)got.HttpStatusCode, got.ContentRange, got.ContentLength, await S3Fidelity.ReadBody(got));
    }

    private sealed record RangedRead(int Status, string? ContentRange, long ContentLength, byte[] Body)
    {
        public bool Equals((int Status, string ContentRange, long ContentLength, byte[] Body) want) =>
            Status == want.Status && ContentRange == want.ContentRange && ContentLength == want.ContentLength && Body.AsSpan().SequenceEqual(want.Body);
    }
}
