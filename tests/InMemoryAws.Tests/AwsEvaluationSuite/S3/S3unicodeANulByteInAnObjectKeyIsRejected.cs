// Translated from behavioral/s3/check_s3unicode_a_nul_byte_in_an_object_key_is_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3unicode-a-nul-byte-in-an-object-key-is-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3unicodeANulByteInAnObjectKeyIsRejected
{
    /// <summary>
    /// A NUL byte in an object key is refused with a bare HTTP 400 and no S3 error code, and no object is
    /// created, while the neighbouring C0 control U+0001 in the same position is stored, readable and
    /// listed: NUL is the one C0 control S3 refuses.
    /// </summary>
    [Test]
    [DisplayName("s3.s3unicode-a-nul-byte-in-an-object-key-is-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        const string prefix = "nul/";
        const string keySoh = $"{prefix}a\u0001b.txt"; // U+0001: accepted
        const string keyNul = $"{prefix}a\u0000b.txt"; // U+0000: refused
        byte[] body = [.. "c0-control-payload"u8, 0x00, 0xff];

        // --- Positive control: the adjacent C0 control is a legal key character.
        await Put(s3, bucket, keySoh, body);
        var got = await S3Fidelity.GetBytes(s3, bucket, keySoh);
        Ensure(got.AsSpan().SequenceEqual(body), $"the U+0001 key did not round-trip its body: sent {Py.Repr(body)}, got {Py.Repr(got)}");
        var listed = await S3CoreFidelity.ListAllKeys(s3, bucket, prefix);
        Ensure(listed.SequenceEqual([keySoh]), $"ListObjectsV2 did not return exactly the U+0001 key: expected [{Py.Repr(keySoh)}], got {Py.Repr(listed)}");
        Ensure(
            Encoding.UTF8.GetBytes(listed[0]).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(keySoh)),
            $"the listed U+0001 key's bytes differ from the written key's: listed {Py.Repr(Encoding.UTF8.GetBytes(listed[0]))}, written {Py.Repr(Encoding.UTF8.GetBytes(keySoh))}");

        // --- Break exactly one thing: the same key with U+0000 instead of U+0001.
        Ensure(keyNul.Replace('\u0000', '\u0001') == keySoh, $"test setup error: {Py.Repr(keyNul)} and {Py.Repr(keySoh)} differ in more than the one byte");
        var exc = await FidelityContext.CaptureError(() => Put(s3, bucket, keyNul, body));
        Ensure(exc is not null, $"PutObject accepted a key containing a NUL byte ({Py.Repr(keyNul)}); real S3 refuses it");
        var status = (int)exc!.StatusCode;
        Ensure(status == 400, $"expected HTTP 400 for a NUL byte in the key, got {status}");
        // botocore reports an error response with no S3 error code by its status, "400"
        var (code, _) = S3Fidelity.Botocore(exc);
        Ensure(
            code == "400",
            $"expected the errorless-400 stand-in code '400' for a NUL byte in the key, got {Py.Repr(code)} -- real S3 returns no modelled S3 error code here");

        // --- Effect absent: nothing was created, and the key is not even addressable.
        exc = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, keyNul));
        Ensure(exc is not null, $"the NUL-byte key {Py.Repr(keyNul)} is readable via HeadObject; the refused write landed anyway");
        var after = await S3CoreFidelity.ListAllKeys(s3, bucket, prefix);
        Ensure(after.SequenceEqual([keySoh]), $"after the refused NUL-byte write the prefix should still hold exactly {Py.Repr(keySoh)}, got {Py.Repr(after)}");

        // --- Positive control still holds: the refusal did not disturb the U+0001 object.
        var final = await S3Fidelity.GetBytes(s3, bucket, keySoh);
        Ensure(final.AsSpan().SequenceEqual(body), $"the U+0001 object was altered by the refused NUL-byte write: expected {Py.Repr(body)}, got {Py.Repr(final)}");
    }

    private static Task<PutObjectResponse> Put(IAmazonS3 s3, string bucket, string key, byte[] body) =>
        s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body) });
}
