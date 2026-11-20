// Translated from behavioral/s3/check_s3unicode_object_tag_value_with_an_astral_emoji_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3unicode-object-tag-value-with-an-astral-emoji-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3unicodeObjectTagValueWithAnAstralEmojiRejected
{
    /// <summary>
    /// Object tag values accept NFC accented letters and CJK byte-exactly, but an astral-plane
    /// character and an NFD combining mark are each refused with InvalidTag, leaving the tag set as is.
    /// Reference: https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-tagging.html
    /// </summary>
    [Test]
    [DisplayName("s3.s3unicode-object-tag-value-with-an-astral-emoji-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        const string emoji = "\U0001f600";
        const string key = "tag/obj.bin";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "tagged" });

        // Built from codepoints so no editor or formatter can silently normalise the NFD form to NFC.
        var nfc = "caf" + (char)0x00E9;                  // LATIN SMALL LETTER E WITH ACUTE
        var nfd = "caf" + (char)0x0065 + (char)0x0301;   // LATIN SMALL LETTER E + COMBINING ACUTE ACCENT
        var cjk = new string([(char)0x65E5, (char)0x672C, (char)0x8A9E]); // CJK, three UTF-8 bytes each
        Ensure(nfd.Normalize(NormalizationForm.FormC) == nfc && nfd != nfc, $"test setup error: {Py.Repr(nfd)} is not the NFD form of {Py.Repr(nfc)}");

        // --- Accepted side: three non-ASCII values that S3 does allow, stored byte-exactly.
        List<Tag> accepted =
        [
            new() { Key = "sim2real-plain", Value = "PLAIN-ASCII" },
            new() { Key = "sim2real-nfc", Value = nfc },
            new() { Key = "sim2real-cjk", Value = cjk },
        ];
        await Tag(s3, bucket, key, accepted);
        var asMap = await TagMap(s3, bucket, key);
        var wrote = accepted.ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);
        Ensure(Same(asMap, wrote), $"the accepted tag set did not round-trip: wrote {Show(wrote)}, read {Show(asMap)}");
        Ensure(
            Encoding.UTF8.GetBytes(asMap["sim2real-nfc"]).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(nfc)),
            $"the precomposed tag value was re-encoded: wrote {Py.Repr(Encoding.UTF8.GetBytes(nfc))}, read {Py.Repr(Encoding.UTF8.GetBytes(asMap["sim2real-nfc"]))}");
        Ensure(
            Encoding.UTF8.GetBytes(asMap["sim2real-cjk"]).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(cjk)),
            $"the CJK tag value was re-encoded: wrote {Py.Repr(Encoding.UTF8.GetBytes(cjk))}, read {Py.Repr(Encoding.UTF8.GetBytes(asMap["sim2real-cjk"]))}");

        // --- Break exactly one thing: one tag value becomes an astral-plane character.
        var exc = await FidelityContext.CaptureError(() => Tag(s3, bucket, key, [new Tag { Key = "sim2real-astral", Value = emoji }]));
        Ensure(
            exc is not null,
            $"PutObjectTagging accepted the astral-plane tag value {Py.Repr(emoji)}; real S3 refuses it, so the tag-value character class is not being enforced");
        Ensure(exc!.Code == "InvalidTag", $"expected InvalidTag for an astral-plane tag value, got {Py.Repr(exc.Code)}");
        Ensure(exc.Message == "The TagValue you have provided is invalid", $"unexpected message {Py.Repr(exc.Message)}");
        Ensure((int)exc.StatusCode == 400, $"expected HTTP 400 for InvalidTag, got {(int)exc.StatusCode}");

        // --- Effect absent: the refused call replaced nothing. PutObjectTagging is a whole-set
        //     replacement, so a partially applied refusal would be visible as a lost tag set.
        var afterAstral = await TagMap(s3, bucket, key);
        Ensure(Same(afterAstral, asMap), $"the refused astral tag value changed the stored tag set: was {Show(asMap)}, now {Show(afterAstral)}");

        // --- The same boundary, from the other side of Unicode equivalence: the NFD spelling of an
        //     ACCEPTED value is refused with the same code.
        exc = await FidelityContext.CaptureError(() => Tag(s3, bucket, key, [new Tag { Key = "sim2real-nfd", Value = nfd }]));
        Ensure(
            exc is not null,
            $"PutObjectTagging accepted the decomposed tag value {Py.Repr(nfd)} while real S3 refuses it, even though it accepts the canonically equivalent {Py.Repr(nfc)}; the validator is normalising before it checks");
        Ensure(exc!.Code == "InvalidTag", $"expected InvalidTag for a tag value containing a combining mark, got {Py.Repr(exc.Code)}");
        Ensure(exc.Message == "The TagValue you have provided is invalid", $"unexpected message {Py.Repr(exc.Message)}");

        // --- Effect absent again, and positive control: the accepted set is still exactly what it was.
        var final = await TagMap(s3, bucket, key);
        Ensure(Same(final, asMap), $"the refused decomposed tag value changed the stored tag set: was {Show(asMap)}, now {Show(final)}");
        var content = Encoding.ASCII.GetString(await S3Fidelity.GetBytes(s3, bucket, key));
        Ensure(content == "tagged", "the object's content was disturbed by the refused tagging calls");
    }

    private static Task Tag(IAmazonS3 s3, string bucket, string key, List<Tag> tags) =>
        s3.PutObjectTaggingAsync(new PutObjectTaggingRequest { BucketName = bucket, Key = key, Tagging = new Tagging { TagSet = tags } });

    private static async Task<Dictionary<string, string>> TagMap(IAmazonS3 s3, string bucket, string key) =>
        ((await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key })).Tagging ?? [])
            .ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);

    private static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && v == p.Value);

    private static string Show(Dictionary<string, string> map) =>
        "{" + string.Join(", ", map.Select(p => $"{Py.Repr(p.Key)}: {Py.Repr(p.Value)}")) + "}";
}
