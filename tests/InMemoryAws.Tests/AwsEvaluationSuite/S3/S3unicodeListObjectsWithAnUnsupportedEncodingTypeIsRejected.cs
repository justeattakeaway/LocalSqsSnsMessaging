// Translated from behavioral/s3/check_s3unicode_list_objects_with_an_unsupported_encoding_type_is_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3unicode-list-objects-with-an-unsupported-encoding-type-is-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3unicodeListObjectsWithAnUnsupportedEncodingTypeIsRejected
{
    /// <summary>
    /// ListObjectsV2 accepts only EncodingType=url: utf8 is refused with exactly InvalidArgument and no
    /// unencoded listing comes back, while url succeeds on a key holding a newline and an astral
    /// character, the case EncodingType exists for.
    /// </summary>
    [Test]
    [DisplayName("s3.s3unicode-list-objects-with-an-unsupported-encoding-type-is-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");

        const string emoji = "\U0001f600";
        const string prefix = "encneg/";
        const string key = $"{prefix}a\nb-{emoji}.txt";
        var body = "encoding-type-payload"u8.ToArray();
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body) });

        // --- Positive control: the one supported value encodes the newline and the astral character,
        //     and unquote_plus recovers the stored key exactly.
        var encoded = await S3Fidelity.ListKeys(ctx, bucket, prefix, "url");
        Ensure(encoded.SequenceEqual([$"{prefix}a%0Ab-%F0%9F%98%80.txt"]), $"EncodingType=url did not encode the key as expected: got {Py.Repr(encoded)}");
        Ensure(
            S3Fidelity.UnquotePlus(encoded[0]) == key,
            $"the url-encoded key does not decode back to the stored key: decoded {Py.Repr(S3Fidelity.UnquotePlus(encoded[0]))}, stored {Py.Repr(key)}");

        // --- Break exactly one thing: ask for an encoding S3 does not support.
        // An unexpected success raises inside the helper: real S3 supports only 'url', and silently
        // ignoring an unknown value hands the caller raw keys it will then decode and corrupt.
        var exc = await S3Fidelity.ExpectError(() => s3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket,
            Prefix = prefix,
            Encoding = new EncodingType("utf8"),
        }));
        Ensure(exc.Code == "InvalidArgument", $"expected InvalidArgument for EncodingType='utf8', got {Py.Repr(exc.Code)}");
        Ensure(exc.Message == "Invalid Encoding Method specified in Request", exc.Message ?? "None");
        Ensure((int)exc.StatusCode == 400, $"expected HTTP 400 for InvalidArgument, got {(int)exc.StatusCode}");

        // --- Effect absent: the refusal returned no listing at all, rather than an unencoded one.
        // (the .NET SDK keeps no body of an error response; the same request again, raw)
        using (var refused = await ctx.RawHttpClient(InMemoryAws.Http.AwsServiceType.S3).GetAsync(
            new Uri($"{bucket}?list-type=2&prefix={Uri.EscapeDataString(prefix)}&encoding-type=utf8", UriKind.Relative), ctx.CancellationToken))
        {
            var text = await refused.Content.ReadAsStringAsync(ctx.CancellationToken);
            Ensure(!text.Contains("<Contents>", StringComparison.Ordinal), $"the refused ListObjectsV2 still carried a listing in its error response: {Py.Repr(text)}");
        }

        // --- The bucket is unchanged, and both the unencoded and the supported encoded listing still
        //     report the key byte-exactly.
        var raw = await S3Fidelity.ListKeys(ctx, bucket, prefix);
        Ensure(raw.SequenceEqual([key]), $"the unencoded listing is not byte-exact after the refusal: expected [{Py.Repr(key)}], got {Py.Repr(raw)}");
        var again = await S3Fidelity.ListKeys(ctx, bucket, prefix, "url");
        Ensure(again.SequenceEqual(encoded), $"EncodingType=url stopped working after the refused call: expected {Py.Repr(encoded)}, got {Py.Repr(again)}");
        Ensure((await S3Fidelity.GetBytes(s3, bucket, key)).AsSpan().SequenceEqual(body), "the object's content was disturbed by the refused listing");
    }
}
