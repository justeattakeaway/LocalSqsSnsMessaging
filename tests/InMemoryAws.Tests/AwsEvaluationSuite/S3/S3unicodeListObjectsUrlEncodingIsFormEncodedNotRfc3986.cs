// Translated from behavioral/s3/check_s3unicode_list_objects_url_encoding_is_form_encoded_not_rfc3986.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3unicode-list-objects-url-encoding-is-form-encoded-not-rfc3986</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3unicodeListObjectsUrlEncodingIsFormEncodedNotRfc3986
{
    /// <summary>
    /// EncodingType=url form-encodes keys (space to <c>+</c>, <c>+</c> to <c>%2B</c>), not RFC 3986.
    /// Only <c>unquote_plus</c> recovers the keys; plain <c>unquote</c> turns <c>a b</c> into existing <c>a+b</c>.
    /// Delimiter and CommonPrefixes follow the same rule; the unencoded listing is byte-exact.
    /// Reference: https://docs.aws.amazon.com/AmazonS3/latest/API/API_ListObjectsV2.html
    /// </summary>
    [Test]
    [DisplayName("s3.s3unicode-list-objects-url-encoding-is-form-encoded-not-rfc3986")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        const string emoji = "\U0001f600";
        const string prefix = "enc/";
        // One key per encoding hazard. The first two are the pair that collide under the wrong decoder.
        var keys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"{prefix}a b.txt"] = "space",
            [$"{prefix}a+b.txt"] = "literal-plus",
            [$"{prefix}a%20b.txt"] = "literal-percent",
            [$"{prefix}a\nb.txt"] = "newline",
            [$"{prefix}a\u0001b.txt"] = "c0-control",
            [$"{prefix}a{emoji}b.txt"] = "astral",
        };
        Ensure(keys.Count == 6, $"test setup error: expected 6 distinct keys, got {keys.Count}");
        foreach (var (key, body) in keys)
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = body });
        }

        var sortedKeys = Sorted(keys.Keys);

        // --- Positive control: with no EncodingType the keys come back raw and byte-exact, including
        //     the newline and the C0 control (S3 XML-escapes them rather than dropping them).
        var raw = Sorted(await S3Fidelity.ListKeys(ctx, bucket, prefix));
        Ensure(raw.SequenceEqual(sortedKeys), $"the unencoded ListObjectsV2 listing is not byte-exact: expected {Py.Repr(sortedKeys)}, got {Py.Repr(raw)}");

        // --- EncodingType=url: every key is recoverable EXACTLY, with the form decoder.
        var encoded = Sorted(await S3Fidelity.ListKeys(ctx, bucket, prefix, "url"));
        Ensure(encoded.Count == keys.Count, $"the url-encoded listing returned {encoded.Count} keys, not {keys.Count}: {Py.Repr(encoded)}");
        var recovered = Sorted(encoded.Select(S3Fidelity.UnquotePlus));
        Ensure(
            recovered.SequenceEqual(sortedKeys),
            $"unquote_plus over the url-encoded listing did not recover the stored keys: expected {Py.Repr(sortedKeys)}, got {Py.Repr(recovered)} from {Py.Repr(encoded)}");

        // --- The transform is form-encoding, named byte for byte.
        var byRecovered = encoded.ToDictionary(S3Fidelity.UnquotePlus, k => k, StringComparer.Ordinal);
        Ensure(
            byRecovered[$"{prefix}a b.txt"] == $"{prefix}a+b.txt",
            $"a space in a key was not encoded as '+': the stored key {Py.Repr(prefix + "a b.txt")} came back as {Py.Repr(byRecovered[prefix + "a b.txt"])}");
        Ensure(
            byRecovered[$"{prefix}a+b.txt"] == $"{prefix}a%2Bb.txt",
            $"a literal '+' in a key was not encoded as '%2B': the stored key {Py.Repr(prefix + "a+b.txt")} came back as {Py.Repr(byRecovered[prefix + "a+b.txt"])}");
        Ensure(
            byRecovered[$"{prefix}a%20b.txt"] == $"{prefix}a%2520b.txt",
            $"a literal '%' in a key was not encoded as '%25': the stored key {Py.Repr(prefix + "a%20b.txt")} came back as {Py.Repr(byRecovered[prefix + "a%20b.txt"])}");
        var encNewline = byRecovered[$"{prefix}a\nb.txt"];
        Ensure(encNewline == $"{prefix}a%0Ab.txt", $"a newline in a key was not encoded as '%0A': it came back as {Py.Repr(encNewline)}");
        var encControl = byRecovered[$"{prefix}a\u0001b.txt"];
        Ensure(encControl == $"{prefix}a%01b.txt", $"a C0 control in a key was not encoded as '%01': it came back as {Py.Repr(encControl)}");
        var encAstral = byRecovered[$"{prefix}a{emoji}b.txt"];
        Ensure(encAstral == $"{prefix}a%F0%9F%98%80b.txt", $"the astral key was not encoded as its UTF-8 bytes: it came back as {Py.Repr(encAstral)}");

        // --- Effect absent, and the reason this matters: the RFC-3986 decoder does NOT recover the
        //     space key -- it silently produces the OTHER key that exists in this same bucket.
        var encodedSpace = byRecovered[$"{prefix}a b.txt"];
        var rfc3986Decoded = Uri.UnescapeDataString(encodedSpace);
        Ensure(
            rfc3986Decoded != $"{prefix}a b.txt",
            $"plain unquote recovered the space key {Py.Repr(prefix + "a b.txt")} from {Py.Repr(encodedSpace)}, so S3 is percent-encoding the space rather than form-encoding it");
        Ensure(
            rfc3986Decoded == $"{prefix}a+b.txt",
            $"plain unquote of {Py.Repr(encodedSpace)} yielded {Py.Repr(rfc3986Decoded)}; expected it to collide with the literal-plus key {Py.Repr(prefix + "a+b.txt")}");
        // The collision is real, not hypothetical: that other key holds a different body.
        var collided = Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket, rfc3986Decoded));
        Ensure(
            collided == "literal-plus",
            $"the key an RFC-3986 decoder would produce holds {Py.Repr(collided)}, not the literal-plus body; the collision assertion is not measuring what it claims");
        Ensure(Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket, $"{prefix}a b.txt")) == "space", "the space key no longer holds its own body");

        // --- Delimiter and CommonPrefixes are encoded by the same rule, in the same response.
        // (raw: the .NET SDK decodes a url-encoded listing it is handed, which botocore leaves alone
        // when the caller asked for the encoding)
        var encResp = await S3Fidelity.RawListObjectsV2(ctx, bucket, prefix, delimiter: "\n", encodingType: "url");
        Ensure((string?)encResp.Element(S3Fidelity.S3Xml + "IsTruncated") == "false", "the delimiter listing paged; this assertion needs the whole result in one response");
        var echoedDelimiter = (string?)encResp.Element(S3Fidelity.S3Xml + "Delimiter");
        Ensure(echoedDelimiter == "%0A", $"the echoed Delimiter was not url-encoded: got {Py.Repr(echoedDelimiter)}, expected '%0A'");
        var common = encResp.Elements(S3Fidelity.S3Xml + "CommonPrefixes").Select(cp => (string)cp.Element(S3Fidelity.S3Xml + "Prefix")!).ToList();
        Ensure(common.SequenceEqual([$"{prefix}a%0A"]), $"CommonPrefixes was not url-encoded as expected: got {Py.Repr(common)}, expected ['{prefix}a%0A']");
        Ensure(
            S3Fidelity.UnquotePlus(common[0]) == $"{prefix}a\n",
            $"the encoded common prefix does not decode back to the stored bytes: {Py.Repr(S3Fidelity.UnquotePlus(common[0]))}");

        // --- Positive control for the same two fields with no EncodingType: raw bytes, not encoded.
        var rawResp = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = prefix, Delimiter = "\n" });
        Ensure(rawResp.IsTruncated == false, "the unencoded delimiter listing paged; this assertion needs the whole result in one response");
        Ensure(rawResp.Delimiter == "\n", $"the unencoded listing altered the echoed Delimiter: got {Py.Repr(rawResp.Delimiter)}");
        var rawCommon = (rawResp.CommonPrefixes ?? []).ToList();
        Ensure(rawCommon.SequenceEqual([$"{prefix}a\n"]), $"the unencoded CommonPrefixes is not byte-exact: got {Py.Repr(rawCommon)}");
    }

    private static List<string> Sorted(IEnumerable<string> keys) => [.. keys.Order(StringComparer.Ordinal)];
}
