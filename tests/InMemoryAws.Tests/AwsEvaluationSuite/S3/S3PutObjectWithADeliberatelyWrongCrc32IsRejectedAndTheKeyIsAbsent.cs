// Translated from behavioral/s3/check_s3_put_object_with_a_deliberately_wrong_crc32_is_rejected_and_the_key_is_absent.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-put-object-with-a-deliberately-wrong-crc32-is-rejected-and-the-key-is-absent</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3PutObjectWithADeliberatelyWrongCrc32IsRejectedAndTheKeyIsAbsent
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("the-bytes-whose-checksum-s3-must-recompute-rather-than-store", 64)));

    /// <summary>
    /// A supplied CRC32 is recomputed over the body, not stored; a mismatch is BadDigest.
    /// The key stays absent, and the stored checksum equals a client-side zlib CRC32 of the bytes.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-put-object-with-a-deliberately-wrong-crc32-is-rejected-and-the-key-is-absent")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "checksum-verified.bin";

        var correct = S3Fidelity.Crc32B64(Payload);
        var wrong = S3Fidelity.Crc32B64([.. Payload, (byte)'x']);
        Ensure(wrong != correct, "the wrong digest collided with the correct one");
        Ensure(Convert.FromBase64String(wrong).Length == 4, $"the wrong digest must still be a well-formed 4-byte CRC32, got {Convert.FromBase64String(wrong).Length} bytes");

        // A well-formed CRC32 of a payload one byte longer: only server recomputation can refuse it.
        var exc = await FidelityContext.CaptureError(() => s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(Payload), ChecksumCRC32 = wrong }));
        Ensure(
            exc is not null,
            $"PutObject accepted ChecksumCRC32={Py.Repr(wrong)} for a body whose actual CRC32 is {Py.Repr(correct)} — the header is being stored rather than verified, so every corrupted upload on the target is silently accepted");
        Ensure(
            (exc!.Code, exc.Message) == ("BadDigest", "The CRC32 you specified did not match the calculated checksum."),
            $"expected BadDigest with the CRC32 mismatch message, got {exc}");
        Ensure(exc.StatusCode == HttpStatusCode.BadRequest, $"expected HTTP 400 for BadDigest, got {(int)exc.StatusCode}");

        // --- Effect absent: nothing was stored at the key.
        var missing = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, key));
        Ensure(missing is not null, "the rejected PutObject stored the object anyway");
        Ensure(missing!.StatusCode == HttpStatusCode.NotFound, $"HeadObject after the refusal returned {(int)missing.StatusCode}, expected 404");
        var listed = (await S3Fidelity.ListedObjects(s3, bucket)).Select(o => o.Key).ToList();
        Ensure(listed.Count == 0, $"the bucket holds {Py.Repr(listed)} after the refused PUT; it must be empty");

        // --- POSITIVE CONTROL: the identical bytes with the correct zlib-computed CRC32.
        var stored = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(Payload), ChecksumCRC32 = correct });
        Ensure(stored.ChecksumCRC32 == correct, $"the accepted PutObject echoed {Py.Repr(stored.ChecksumCRC32)}, the client computed {Py.Repr(correct)}");
        Ensure(stored.ChecksumType == ChecksumType.FULL_OBJECT, $"a single-part PutObject checksum is a whole-object one; got ChecksumType {Py.Repr(stored.ChecksumType?.Value)}");

        // --- And the VALUE survives a read, compared to the client computation, not to itself.
        using var got = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, ChecksumMode = ChecksumMode.ENABLED });
        Ensure(
            got.ChecksumCRC32 == correct,
            $"GetObject with ChecksumMode=ENABLED returned {Py.Repr(got.ChecksumCRC32)}; the client computed {Py.Repr(correct)} with zlib.crc32 over the same {Payload.Length} bytes");
        var body = await S3Fidelity.ReadBody(got);
        Ensure(body.AsSpan().SequenceEqual(Payload), $"the stored object is {body.Length} bytes, want {Payload.Length}");
        Ensure(
            S3Fidelity.Crc32B64(body) == got.ChecksumCRC32,
            $"the bytes S3 returned checksum to {Py.Repr(S3Fidelity.Crc32B64(body))} but S3 reported {Py.Repr(got.ChecksumCRC32)} for them");
    }
}
