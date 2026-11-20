// Translated from behavioral/s3/check_s3unicode_rfc3986_reserved_chars_in_a_key_are_literal.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3unicode-rfc3986-reserved-chars-in-a-key-are-literal</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3unicodeRfc3986ReservedCharsInAKeyAreLiteral
{
    /// <summary>
    /// RFC-3986 reserved characters in a key are stored, listed and prefix-matched literally: <c>+</c>
    /// is not a space and <c>%20</c> is three characters, so neither space spelling finds the object.
    /// </summary>
    [Test]
    [DisplayName("s3.s3unicode-rfc3986-reserved-chars-in-a-key-are-literal")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        const string prefix = "res/";
        // Every RFC-3986 reserved character that S3 permits in a key, plus a literal percent
        // sequence, a literal plus and a literal space.
        const string tail = "a+b c%20d?e#f&g=h i@j!k'l(m)n,o$p;q=r[s]t\"u~v.txt";
        const string key = $"{prefix}{tail}";
        byte[] body = [.. "reserved-character-payload"u8, 0x00, 0xff];

        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body) });

        // --- The bytes round-trip.
        var got = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(
            got.AsSpan().SequenceEqual(body),
            $"GetObject on the reserved-character key did not return the body byte-for-byte: sent {Py.Repr(body)}, got {Py.Repr(got)}");

        // --- The key lists back with identical bytes.
        var listed = await S3Fidelity.ListKeys(ctx, bucket, prefix);
        Ensure(listed.SequenceEqual([key]), $"ListObjectsV2 did not return exactly the reserved-character key: expected [{Py.Repr(key)}], got {Py.Repr(listed)}");
        Ensure(
            Encoding.UTF8.GetBytes(listed[0]).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(key)),
            $"the listed key's bytes differ from the written key's: listed {Py.Repr(Encoding.UTF8.GetBytes(listed[0]))}, written {Py.Repr(Encoding.UTF8.GetBytes(key))}");

        // --- A SERVER-SIDE prefix match over the reserved bytes selects it. This is the assertion a
        //     client-side comparison cannot fake: the prefix itself carries '+', ' ' and '%20'.
        const string serverPrefix = $"{prefix}a+b c%20";
        var matched = await S3Fidelity.ListKeys(ctx, bucket, serverPrefix);
        Ensure(matched.SequenceEqual([key]), $"a server-side Prefix of {Py.Repr(serverPrefix)} did not select the key: expected [{Py.Repr(key)}], got {Py.Repr(matched)}");

        // --- Effect absent 1: the '+'-as-space spelling is not an alias (form-decoding bug).
        var keyPlusAsSpace = key.Replace("a+b", "a b", StringComparison.Ordinal);
        Ensure(keyPlusAsSpace != key, "test setup error: the '+'-as-space variant equals the key");
        var exc = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, keyPlusAsSpace));
        Ensure(
            exc is not null,
            $"the key spelled with a space where the stored key has a '+' resolves to an object: {Py.Repr(keyPlusAsSpace)} -- '+' is being form-decoded as a space");
        Ensure((int)exc!.StatusCode == 404, $"expected HTTP 404 for the '+'-as-space spelling, got {(int)exc.StatusCode}");

        // --- Effect absent 2: the '%20'-as-space spelling is not an alias (double-decoding bug).
        var keyPctAsSpace = key.Replace("c%20d", "c d", StringComparison.Ordinal);
        Ensure(keyPctAsSpace != key, "test setup error: the '%20'-as-space variant equals the key");
        exc = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, keyPctAsSpace));
        Ensure(
            exc is not null,
            $"the key spelled with a space where the stored key has the three literal characters '%20' resolves to an object: {Py.Repr(keyPctAsSpace)} -- the key is being percent-decoded twice");
        Ensure((int)exc!.StatusCode == 404, $"expected HTTP 404 for the '%20'-as-space spelling, got {(int)exc.StatusCode}");

        // --- Positive control: the literal key is still exactly one object holding exactly its bytes.
        var after = await S3Fidelity.ListKeys(ctx, bucket, prefix);
        Ensure(after.SequenceEqual([key]), $"probing the corrupted spellings changed the bucket: {Py.Repr(after)}");
        var final = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(final.AsSpan().SequenceEqual(body), $"the reserved-character object was altered by the 404 probes: expected {Py.Repr(body)}, got {Py.Repr(final)}");
    }
}
