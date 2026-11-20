// Translated from behavioral/s3/check_s3unicode_carriage_return_in_a_key_survives_the_list_xml_byte_exact.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3unicode-carriage-return-in-a-key-survives-the-list-xml-byte-exact</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3unicodeCarriageReturnInAKeySurvivesTheListXmlByteExact
{
    /// <summary>
    /// A CR (U+000D) in a key lists back as CR, not LF; the CR and LF keys are two distinct objects.
    /// XML parsers normalise a literal CR to LF, so S3 must escape it as &amp;#13;; EncodingType=url gives
    /// %0D.
    /// </summary>
    [Test]
    [DisplayName("s3.s3unicode-carriage-return-in-a-key-survives-the-list-xml-byte-exact")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        const string prefix = "cr/";
        const string keyCr = $"{prefix}a\rb.txt";
        const string keyLf = $"{prefix}a\nb.txt";
        var bodyCr = "body-CARRIAGE-RETURN"u8.ToArray();
        var bodyLf = "body-LINE-FEED"u8.ToArray();

        Ensure(
            keyCr != keyLf && keyCr.Replace('\r', '\n') == keyLf,
            $"test setup error: {Py.Repr(keyCr)} and {Py.Repr(keyLf)} do not differ in exactly the CR/LF byte");

        // --- Only the CR key exists at first, so the LF spelling 404ing proves they are distinct.
        await Put(s3, bucket, keyCr, bodyCr);
        var exc = await FidelityContext.CaptureError(() => s3.GetObjectMetadataAsync(bucket, keyLf));
        Ensure(
            exc is not null,
            $"the line-feed spelling {Py.Repr(keyLf)} resolves to an object while only the carriage-return key was written; CR is being folded to LF");
        Ensure((int)exc!.StatusCode == 404, $"expected HTTP 404 for the line-feed spelling, got {(int)exc.StatusCode}");

        // --- The unencoded listing returns the carriage return itself, which the response XML can only
        //     achieve by escaping it as &#13;.
        var listed = await S3Fidelity.ListKeys(ctx, bucket, prefix);
        Ensure(
            listed.SequenceEqual([keyCr]),
            $"ListObjectsV2 did not return the carriage-return key byte-exactly: expected [{Py.Repr(keyCr)}], got {Py.Repr(listed)} -- a line feed here means the key was written into the response XML unescaped and the parser normalised it");
        Ensure(
            Encoding.UTF8.GetBytes(listed[0]).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(keyCr)),
            $"the listed key's bytes differ from the written key's: listed {Py.Repr(Encoding.UTF8.GetBytes(listed[0]))}, written {Py.Repr(Encoding.UTF8.GetBytes(keyCr))}");

        // --- Independent confirmation over the transport with no XML-escaping question: %0D, not %0A.
        var encoded = await S3Fidelity.ListKeys(ctx, bucket, prefix, "url");
        Ensure(encoded.SequenceEqual([$"{prefix}a%0Db.txt"]), $"EncodingType=url did not report the key's CR as %0D: got {Py.Repr(encoded)}");
        Ensure(
            S3Fidelity.UnquotePlus(encoded[0]) == keyCr,
            $"the url-encoded key does not decode back to the carriage-return key: decoded {Py.Repr(S3Fidelity.UnquotePlus(encoded[0]))}, stored {Py.Repr(keyCr)}");

        // --- Both spellings now coexist as two objects with their own bodies.
        await Put(s3, bucket, keyLf, bodyLf);
        var both = (await S3Fidelity.ListKeys(ctx, bucket, prefix)).Order(StringComparer.Ordinal).ToList();
        var expected = new[] { keyCr, keyLf }.Order(StringComparer.Ordinal).ToList();
        Ensure(both.SequenceEqual(expected), $"the CR and LF keys are not two distinct objects: expected {Py.Repr(expected)}, got {Py.Repr(both)}");
        var gotCr = await S3Fidelity.GetBytes(s3, bucket, keyCr);
        Ensure(gotCr.AsSpan().SequenceEqual(bodyCr), $"the carriage-return key returned the wrong body: expected {Py.Repr(bodyCr)}, got {Py.Repr(gotCr)}");
        var gotLf = await S3Fidelity.GetBytes(s3, bucket, keyLf);
        Ensure(gotLf.AsSpan().SequenceEqual(bodyLf), $"the line-feed key returned the wrong body: expected {Py.Repr(bodyLf)}, got {Py.Repr(gotLf)}");

        // --- Transition: deleting the LF key leaves the CR key whole.
        await s3.DeleteObjectAsync(bucket, keyLf);
        var after = await S3Fidelity.ListKeys(ctx, bucket, prefix);
        Ensure(after.SequenceEqual([keyCr]), $"deleting the line-feed key disturbed the carriage-return key: {Py.Repr(after)}");
        var survivor = await S3Fidelity.GetBytes(s3, bucket, keyCr);
        Ensure(survivor.AsSpan().SequenceEqual(bodyCr), $"the carriage-return object lost its body: expected {Py.Repr(bodyCr)}, got {Py.Repr(survivor)}");
    }

    private static Task<PutObjectResponse> Put(IAmazonS3 s3, string bucket, string key, byte[] body) =>
        s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body) });
}
