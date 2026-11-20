// Translated from behavioral/s3/check_s3checksum_sha256_mismatch_refuses_the_overwrite_and_the_stored_bytes_survive.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3checksum-sha256-mismatch-refuses-the-overwrite-and-the-stored-bytes-survive</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3checksumSha256MismatchRefusesTheOverwriteAndTheStoredBytesSurvive
{
    private static readonly byte[] V1 = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("version-one-bytes-whose-sha256-the-service-must-compute-", 96)));
    private static readonly byte[] V2 = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("version-two-bytes-are-a-different-length-and-a-different-digest-", 64)));

    /// <summary>
    /// A BadDigest SHA256 overwrite leaves the stored bytes, length, ETag and digest intact.
    /// A digest checked only after committing the bytes would destroy the stored object; the same
    /// overwrite with the true digest then replaces both bytes and digest.
    /// </summary>
    [Test]
    [DisplayName("s3.s3checksum-sha256-mismatch-refuses-the-overwrite-and-the-stored-bytes-survive")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "overwrite-target.bin";

        var v1Digest = S3Fidelity.Sha256B64(V1);
        var v2Digest = S3Fidelity.Sha256B64(V2);
        // Well-formed SHA-256 of a different body, so only recomputing over the body can refuse it.
        var wrong = S3Fidelity.Sha256B64([.. V2, (byte)'x']);
        Ensure(new[] { v1Digest, v2Digest, wrong }.Distinct(StringComparer.Ordinal).Count() == 3, $"the three digests must be distinct, got {Py.Repr(v1Digest)} {Py.Repr(v2Digest)} {Py.Repr(wrong)}");
        Ensure(Convert.FromBase64String(wrong).Length == 32, $"the wrong digest must still be a well-formed 32-byte SHA-256, got {Convert.FromBase64String(wrong).Length} bytes");

        Task<PutObjectResponse> Put(byte[] body, string checksum) =>
            s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body), ChecksumSHA256 = checksum });

        // --- Establish the object that must survive.
        var first = await Put(V1, v1Digest);
        Ensure(first.ChecksumSHA256 == v1Digest, $"PutObject echoed {Py.Repr(first.ChecksumSHA256)}, the client computed {Py.Repr(v1Digest)} with hashlib over the same {V1.Length} bytes");
        var v1Etag = first.ETag;

        // --- Break exactly one thing: the checksum on an otherwise valid overwrite.
        var exc = await FidelityContext.CaptureError(() => Put(V2, wrong));
        Ensure(exc is not null, $"the overwrite was accepted with ChecksumSHA256={Py.Repr(wrong)} for a body whose actual SHA-256 is {Py.Repr(v2Digest)} — the header is stored rather than verified");
        Ensure(exc!.Code == "BadDigest", $"expected BadDigest for a mismatched SHA256, got {Py.Repr(exc.Code)}");
        Ensure(exc.Message == "The SHA256 you specified did not match the calculated checksum.", $"expected the exact BadDigest message for a mismatched SHA256, got {Py.Repr(exc.Message)}");
        Ensure(exc.StatusCode == HttpStatusCode.BadRequest, $"expected HTTP 400 for BadDigest, got {(int)exc.StatusCode}");

        // --- Effect absent: the object is byte-for-byte the one that was there before.
        using (var survivor = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, ChecksumMode = ChecksumMode.ENABLED }))
        {
            var body = await S3Fidelity.ReadBody(survivor);
            Ensure(
                body.AsSpan().SequenceEqual(V1),
                $"the refused overwrite damaged the stored object: it is now {body.Length} bytes starting {Py.Repr(body[..Math.Min(24, body.Length)])}, it must still be the {V1.Length} bytes of version one");
            Ensure(survivor.ContentLength == V1.Length, $"ContentLength is {survivor.ContentLength}, the surviving object must be {V1.Length}");
            Ensure(
                survivor.ChecksumSHA256 == v1Digest,
                $"the object now reports ChecksumSHA256={Py.Repr(survivor.ChecksumSHA256)}; the refused overwrite must leave version one's {Py.Repr(v1Digest)} in place");
            Ensure(survivor.ETag == v1Etag, $"ETag changed from {Py.Repr(v1Etag)} to {Py.Repr(survivor.ETag)} across a refused overwrite");
        }

        var listed = (await S3Fidelity.ListedObjects(s3, bucket)).Select(o => (o.Key, o.Size)).ToList();
        Ensure(
            listed.SequenceEqual([(key, (long)V1.Length)]),
            $"the bucket listing is {Py.Repr(listed.Select(l => l.ToString()).ToList())}; it must hold exactly the one surviving object ({Py.Repr(key)}, {V1.Length})");

        // --- POSITIVE CONTROL: the same overwrite with V2's true digest must go through.
        var second = await Put(V2, v2Digest);
        Ensure(second.ChecksumSHA256 == v2Digest, $"the accepted overwrite echoed {Py.Repr(second.ChecksumSHA256)}, want {Py.Repr(v2Digest)}");
        using var replaced = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, ChecksumMode = ChecksumMode.ENABLED });
        var newBody = await S3Fidelity.ReadBody(replaced);
        Ensure(newBody.AsSpan().SequenceEqual(V2), $"after the accepted overwrite the object is {newBody.Length} bytes, want {V2.Length}");
        Ensure(replaced.ChecksumSHA256 == v2Digest, $"the replaced object reports {Py.Repr(replaced.ChecksumSHA256)}, want {Py.Repr(v2Digest)}");
        Ensure(
            S3Fidelity.Sha256B64(newBody) == replaced.ChecksumSHA256,
            $"the bytes S3 returned hash to {Py.Repr(S3Fidelity.Sha256B64(newBody))} but S3 reported {Py.Repr(replaced.ChecksumSHA256)} for them");
        Ensure(replaced.ETag != v1Etag, $"the accepted overwrite left the ETag at {Py.Repr(v1Etag)}; the bytes changed, so it must not");
    }
}
