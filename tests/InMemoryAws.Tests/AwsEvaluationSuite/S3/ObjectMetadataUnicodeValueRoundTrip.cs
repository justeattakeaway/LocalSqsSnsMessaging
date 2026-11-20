// Translated from behavioral/s3/check_object_metadata_unicode_value_round_trip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-metadata-unicode-value-round-trip</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectMetadataUnicodeValueRoundTrip
{
    /// <summary>
    /// An RFC 2047 encoded-word metadata value (how S3 carries non-ASCII metadata) comes back
    /// byte-identical from HeadObject and GetObject and decodes to the original Unicode text; after
    /// delete the key is NoSuchKey/404 and can be reused.
    /// </summary>
    [Test]
    [DisplayName("s3.object-metadata-unicode-value-round-trip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        const string key = "unicode-metadata.txt";
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-s3meta-uni-");

        const string unicodeValue = "ÄMÄZÕÑ S3 日本語"; // "ÄMÄZÕÑ S3 日本語"
        const string asciiValue = "PLAIN-ASCII-VALUE";

        // Caller-side RFC 2047 mail-safe ("B" = base64) encoding -- this is the
        // ASCII-only representation that is actually transmitted on the wire.
        var encodedValue = "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(unicodeValue)) + "?=";

        var put = new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "payload" };
        put.Metadata.Add("nonascii", encodedValue);
        put.Metadata.Add("ascii", asciiValue);
        await s3.PutObjectAsync(put);

        var head = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(
            head.Metadata["nonascii"] == encodedValue,
            $"expected the RFC-2047 encoded-word to survive byte-identical, sent {Py.Repr(encodedValue)}, got {Py.Repr(head.Metadata["nonascii"])}");
        Ensure(head.Metadata["ascii"] == asciiValue, "assert head[\"Metadata\"][\"ascii\"] == ascii_value");
        Ensure(DecodeRfc2047(head.Metadata["nonascii"]) == unicodeValue, "assert _decode_rfc2047(head[\"Metadata\"][\"nonascii\"]) == unicode_value");

        using (var got = await s3.GetObjectAsync(bucket, key))
        {
            Ensure(
                got.Metadata["nonascii"] == encodedValue,
                $"expected the RFC-2047 encoded-word to survive byte-identical, sent {Py.Repr(encodedValue)}, got {Py.Repr(got.Metadata["nonascii"])}");
            Ensure(got.Metadata["ascii"] == asciiValue, "assert got[\"Metadata\"][\"ascii\"] == ascii_value");
            Ensure(DecodeRfc2047(got.Metadata["nonascii"]) == unicodeValue, "assert _decode_rfc2047(got[\"Metadata\"][\"nonascii\"]) == unicode_value");
        }

        // Cleanup already deletes this object -- make that delete a free negative:
        // prove it is genuinely gone via an independent read, that a second
        // operation on the dead key also fails, and that the key is reusable.
        await s3.DeleteObjectAsync(bucket, key);
        var error = await FidelityContext.CaptureError(() => s3.GetObjectAsync(bucket, key))
            ?? throw Fail("expected GetObject to raise NoSuchKey after DeleteObject");
        Ensure(error.Code == "NoSuchKey", $"expected NoSuchKey for a deleted object, got {Py.Repr(error.Code)}");
        Ensure(error.Message == "The specified key does not exist.", $"unexpected NoSuchKey message: {Py.Repr(error.Message)}");
        error = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, key))
            ?? throw Fail("expected HeadObject to raise 404 after DeleteObject");
        var (code, message) = S3Fidelity.Botocore(error);
        Ensure(code == "404", $"expected 404 for a deleted object, got {Py.Repr(code)}");

        // HeadObject carries no error body; the message is the HTTP reason phrase.
        Ensure(message == "Not Found", $"unexpected HeadObject 404 message: {Py.Repr(message)}");

        var reuse = new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "reused" };
        reuse.Metadata.Add("ascii", asciiValue);
        await s3.PutObjectAsync(reuse);
        var reused = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(reused.Metadata["ascii"] == asciiValue, "a deleted key must be reusable");
    }

    /// <summary>Decode a '=?UTF-8?B?...?=' RFC 2047 encoded-word back to text.</summary>
    private static string DecodeRfc2047(string encoded)
    {
        Ensure(encoded.StartsWith("=?UTF-8?B?", StringComparison.Ordinal) && encoded.EndsWith("?=", StringComparison.Ordinal), encoded);
        var b64Part = encoded["=?UTF-8?B?".Length..^"?=".Length];
        return Encoding.UTF8.GetString(Convert.FromBase64String(b64Part));
    }
}
