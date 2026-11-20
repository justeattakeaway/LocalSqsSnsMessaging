// Translated from behavioral/s3/check_s3checksum_crc32c_mismatch_rejected_and_no_object_lands.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3checksum-crc32c-mismatch-rejected-and-no-object-lands</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3checksumCrc32cMismatchRejectedAndNoObjectLands
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("crc32c-is-recomputed-by-the-service-not-echoed-back-", 128)));

    /// <summary>
    /// PutObject with a well-formed but wrong ChecksumCRC32C is refused with 400 BadDigest and its exact
    /// message and leaves the key absent (HeadObject 404, empty listing), while the same PutObject with the
    /// correct value stores a ChecksumCRC32C equal to a locally computed CRC-32C.
    /// </summary>
    [Test]
    [DisplayName("s3.s3checksum-crc32c-mismatch-rejected-and-no-object-lands")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "crc32c-verified.bin";

        var correct = S3Fidelity.Crc32CB64(Payload);
        var wrong = S3Fidelity.Crc32CB64([.. Payload, (byte)'x']);
        Ensure(wrong != correct, "the wrong digest collided with the correct one; pick other bytes");
        Ensure(Convert.FromBase64String(wrong).Length == 4, $"the wrong digest must still be a well-formed 4-byte CRC32C, got {Convert.FromBase64String(wrong).Length} bytes");
        // A CRC32C that is NOT the zlib CRC32 of the same bytes: an emulator that implements
        // ChecksumCRC32C with zlib.crc32 would accept a value this check never sends.
        var zlibCrc32 = S3Fidelity.Crc32B64(Payload);
        Ensure(correct != zlibCrc32, $"the payload's CRC32C {Py.Repr(correct)} equals its CRC32 {Py.Repr(zlibCrc32)}; the two polynomials must disagree for this check to distinguish them");

        // --- Break exactly one thing: the checksum, over otherwise valid bytes.
        var exc = await FidelityContext.CaptureError(() => s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(Payload), ChecksumCRC32C = wrong }));
        Ensure(
            exc is not null,
            $"PutObject accepted ChecksumCRC32C={Py.Repr(wrong)} for a body whose actual CRC32C is {Py.Repr(correct)} — the header is being stored rather than verified, so every corrupted upload is silently accepted");
        Ensure(exc!.Code == "BadDigest", $"expected BadDigest for a mismatched CRC32C, got {Py.Repr(exc.Code)}");
        Ensure(exc.Message == "The CRC32C you specified did not match the calculated checksum.", $"expected the exact BadDigest message for a mismatched CRC32C, got {Py.Repr(exc.Message)}");
        Ensure(exc.StatusCode == HttpStatusCode.BadRequest, $"expected HTTP 400 for BadDigest, got {(int)exc.StatusCode}");

        // --- Effect absent: nothing was stored at the key.
        var missing = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, key));
        Ensure(missing is not null, "the rejected PutObject stored the object anyway");
        Ensure(missing!.StatusCode == HttpStatusCode.NotFound, $"HeadObject after the refusal returned {(int)missing.StatusCode}, expected 404");
        var listed = (await S3Fidelity.ListedObjects(s3, bucket)).Select(o => o.Key).ToList();
        Ensure(listed.Count == 0, $"the bucket holds {Py.Repr(listed)} after the refused PUT; it must be empty");

        // --- POSITIVE CONTROL: the identical bytes with the correct CRC32C.
        var stored = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(Payload), ChecksumCRC32C = correct });
        Ensure(
            stored.ChecksumCRC32C == correct,
            $"the accepted PutObject echoed {Py.Repr(stored.ChecksumCRC32C)}, the client computed {Py.Repr(correct)} from the Castagnoli polynomial over the same {Payload.Length} bytes");
        Ensure(stored.ChecksumType == ChecksumType.FULL_OBJECT, $"a single-part PutObject checksum is a whole-object one; got ChecksumType {Py.Repr(stored.ChecksumType?.Value)}");

        // --- And the VALUE survives both read paths, compared to the client computation.
        using (var got = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, ChecksumMode = ChecksumMode.ENABLED }))
        {
            Ensure(got.ChecksumCRC32C == correct, $"GetObject with ChecksumMode=ENABLED returned {Py.Repr(got.ChecksumCRC32C)}; the client computed {Py.Repr(correct)} over the same {Payload.Length} bytes");
            var body = await S3Fidelity.ReadBody(got);
            Ensure(body.AsSpan().SequenceEqual(Payload), $"the stored object is {body.Length} bytes, want {Payload.Length}");
            Ensure(
                S3Fidelity.Crc32CB64(body) == got.ChecksumCRC32C,
                $"the bytes S3 returned checksum to {Py.Repr(S3Fidelity.Crc32CB64(body))} but S3 reported {Py.Repr(got.ChecksumCRC32C)} for them");
        }

        var head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key, ChecksumMode = ChecksumMode.ENABLED });
        Ensure(head.ChecksumCRC32C == correct, $"HeadObject with ChecksumMode=ENABLED returned {Py.Repr(head.ChecksumCRC32C)}, want {Py.Repr(correct)}");
        Ensure(head.ChecksumType == ChecksumType.FULL_OBJECT, $"HeadObject reported ChecksumType {Py.Repr(head.ChecksumType?.Value)}, want 'FULL_OBJECT'");
        // ChecksumMode is opt-in: without it the digest is withheld, which is how a client knows it
        // has to ask. An emulator that always returns it is over-permissive in the response shape.
        var quiet = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(
            quiet.ChecksumCRC32C is null,
            $"HeadObject without ChecksumMode=ENABLED returned ChecksumCRC32C={Py.Repr(quiet.ChecksumCRC32C)}; the digest must be withheld unless asked for");
    }
}
