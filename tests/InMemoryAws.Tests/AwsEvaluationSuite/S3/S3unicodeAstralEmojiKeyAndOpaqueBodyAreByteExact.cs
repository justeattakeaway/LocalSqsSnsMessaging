// Translated from behavioral/s3/check_s3unicode_astral_emoji_key_and_opaque_body_are_byte_exact.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3unicode-astral-emoji-key-and-opaque-body-are-byte-exact</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3unicodeAstralEmojiKeyAndOpaqueBodyAreByteExact
{
    /// <summary>
    /// A key holding U+1F600 and a body holding invalid UTF-8 survive Put, Get, Head and ListObjectsV2
    /// byte for byte, the ETag matching the local MD5; the mojibake (latin-1 re-decoded) key is a 404,
    /// since S3 stores keys as UTF-8 bytes and bodies as opaque octets.
    /// </summary>
    [Test]
    [DisplayName("s3.s3unicode-astral-emoji-key-and-opaque-body-are-byte-exact")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        const string emoji = "\U0001f600"; // GRINNING FACE: 4 bytes in UTF-8, a surrogate pair in UTF-16
        const string prefix = "uni/";
        const string key = $"{prefix}{emoji}-café-日本語.bin";
        byte[] body = [0xff, 0xfe, 0x00, 0x01, .. Encoding.UTF8.GetBytes(emoji), 0xc3, 0x28, .. "\r\n"u8];

        // --- Setup assertions: the inputs really are the awkward shapes this check is about.
        Ensure(Encoding.UTF8.GetByteCount(emoji) == 4, $"test setup error: expected a 4-byte UTF-8 character, got {Encoding.UTF8.GetByteCount(emoji)} bytes for {Py.Repr(emoji)}");
        Ensure(
            Encoding.BigEndianUnicode.GetBytes(emoji).AsSpan().SequenceEqual(new byte[] { 0xd8, 0x3d, 0xde, 0x00 }),
            $"test setup error: expected the surrogate pair D83D DE00, got {Py.Repr(Encoding.BigEndianUnicode.GetBytes(emoji))}");
        Exception? decodeFailed = null;
        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(body);
        }
        catch (DecoderFallbackException ex)
        {
            decodeFailed = ex;
        }

        Ensure(decodeFailed is not null, $"test setup error: the body was meant to be invalid UTF-8 but decoded cleanly: {Py.Repr(body)}");

        // --- Store it once. Everything after this is a read.
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body) });

        // --- GetObject returns the exact octets, and the server-side digest agrees with ours.
        using var got = await s3.GetObjectAsync(bucket, key);
        var payload = await S3Fidelity.ReadBody(got);
        Ensure(payload.AsSpan().SequenceEqual(body), $"GetObject did not return the body byte-for-byte: sent {Py.Repr(body)}, got {Py.Repr(payload)}");
        Ensure(
            got.ContentLength == body.Length,
            $"ContentLength disagrees with the stored bytes: header says {got.ContentLength}, the body is {body.Length} bytes");
        Ensure(
            got.ETag.Trim('"') == S3Fidelity.Md5Hex(body),
            $"the server-computed ETag is not the MD5 of the bytes we sent: ETag {Py.Repr(got.ETag)}, md5 {Py.Repr(S3Fidelity.Md5Hex(body))} -- the content was altered in transit or at rest");

        // --- HeadObject agrees with GetObject about the same astral-plane key.
        var head = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(head.ContentLength == body.Length, $"HeadObject reports {head.ContentLength} bytes for a {body.Length}-byte object");
        Ensure(head.ETag == got.ETag, $"HeadObject and GetObject disagree on the ETag of one key: {Py.Repr(head.ETag)} vs {Py.Repr(got.ETag)}");

        // --- ListObjectsV2 returns the key with identical codepoints AND identical UTF-8 bytes.
        var listed = await S3CoreFidelity.ListAllKeys(s3, bucket, prefix);
        Ensure(listed.SequenceEqual([key]), $"ListObjectsV2 did not return exactly the one key written under {Py.Repr(prefix)}: expected [{Py.Repr(key)}], got {Py.Repr(listed)}");
        Ensure(
            Encoding.UTF8.GetBytes(listed[0]).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(key)),
            $"the listed key's UTF-8 bytes differ from the written key's: listed {Py.Repr(Encoding.UTF8.GetBytes(listed[0]))}, written {Py.Repr(Encoding.UTF8.GetBytes(key))}");

        // --- Effect absent: the double-decoded (mojibake) spelling of the same key does not exist.
        var mojibake = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(key));
        Ensure(mojibake != key, $"test setup error: the mojibake spelling is identical to the key, so this arm proves nothing: {Py.Repr(key)}");
        var exc = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, mojibake));
        Ensure(exc is not null, $"the latin-1 mojibake spelling {Py.Repr(mojibake)} of the key resolves to an object; the key was stored double-decoded");
        Ensure((int)exc!.StatusCode == 404, $"expected HTTP 404 for the mojibake key, got {(int)exc.StatusCode}");
        var after = await S3CoreFidelity.ListAllKeys(s3, bucket, prefix);
        Ensure(after.SequenceEqual([key]), $"the bucket gained or lost a key while probing the mojibake spelling: {Py.Repr(after)}");
    }
}
