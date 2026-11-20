using System.Globalization;
using System.Text.RegularExpressions;

namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>What the S3 <c>aws-chunked</c> streaming-upload ports share: hand-built, hand-signed PUTs.</summary>
public static partial class S3Fidelity
{
    /// <summary>
    /// <c>_put_unsigned_trailer(ctx, bucket, key, payload, ...)</c>: PUTs <paramref name="payload"/> as an
    /// <c>aws-chunked</c> <c>STREAMING-UNSIGNED-PAYLOAD-TRAILER</c> body declaring
    /// <paramref name="declaredTrailer"/> in <c>x-amz-trailer</c> and sending
    /// <paramref name="sentTrailerName"/>: <paramref name="sentTrailerValue"/> as its trailer.
    /// </summary>
    public static async Task<(int Status, byte[] Body)> PutUnsignedTrailer(
        FidelityContext ctx,
        string bucket,
        string key,
        byte[] payload,
        string declaredTrailer,
        string sentTrailerName,
        string sentTrailerValue,
        int chunkSize = 64 * 1024)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var target = ObjectUrl(ctx, bucket, key);
        var extra = new Dictionary<string, string>
        {
            ["x-amz-decoded-content-length"] = payload.Length.ToString(CultureInfo.InvariantCulture),
            ["x-amz-trailer"] = declaredTrailer,
        };
        var (headers, _, _) = SignWithPayloadHash(
            ctx.Region, "PUT", new Uri(target).Host, "/" + key, "STREAMING-UNSIGNED-PAYLOAD-TRAILER", ctx.Time.GetUtcNow().UtcDateTime, extra);
        var body = UnsignedTrailerBody(Chunks(payload, chunkSize), sentTrailerName, sentTrailerValue);
        headers["content-encoding"] = "aws-chunked";
        headers["content-length"] = body.Length.ToString(CultureInfo.InvariantCulture);
        var (status, _, answer) = await Send(ctx, "PUT", target, headers, body);
        return (status, answer);
    }

    /// <summary>
    /// <c>_put_signed_chunked(ctx, bucket, key, payload, ...)</c>: PUTs <paramref name="payload"/> as an
    /// <c>aws-chunked</c> <c>STREAMING-AWS4-HMAC-SHA256-PAYLOAD</c> body, with
    /// <c>x-amz-decoded-content-length</c> only when <paramref name="includeDecodedLength"/>, and its first
    /// chunk signature broken when <paramref name="corruptFirstChunkSignature"/>.
    /// </summary>
    public static async Task<(int Status, byte[] Body)> PutSignedChunked(
        FidelityContext ctx,
        string bucket,
        string key,
        byte[] payload,
        bool includeDecodedLength = true,
        bool corruptFirstChunkSignature = false,
        int chunkSize = 64 * 1024)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(payload);
        var target = ObjectUrl(ctx, bucket, key);
        var amzTime = ctx.Time.GetUtcNow().UtcDateTime;
        var extra = includeDecodedLength
            ? new Dictionary<string, string> { ["x-amz-decoded-content-length"] = payload.Length.ToString(CultureInfo.InvariantCulture) }
            : [];
        var (headers, seed, scope) = SignWithPayloadHash(
            ctx.Region, "PUT", new Uri(target).Host, "/" + key, "STREAMING-AWS4-HMAC-SHA256-PAYLOAD", amzTime, extra);
        var body = SignedChunkBody(ctx.Region, Chunks(payload, chunkSize), AmzDate(amzTime), scope, seed);
        if (corruptFirstChunkSignature)
        {
            body = CorruptFirstChunkSignature(body);
        }

        headers["content-encoding"] = "aws-chunked";
        headers["content-length"] = body.Length.ToString(CultureInfo.InvariantCulture);
        var (status, _, answer) = await Send(ctx, "PUT", target, headers, body);
        return (status, answer);
    }

    /// <summary>
    /// <c>_corrupt_first_chunk_signature(body)</c>: flips one hex digit of the first chunk-signature,
    /// breaking only that chunk's link in the chain while leaving its size and bytes untouched.
    /// </summary>
    private static byte[] CorruptFirstChunkSignature(byte[] body)
    {
        var text = System.Text.Encoding.Latin1.GetString(body);
        var match = Regex.Match(text, "chunk-signature=([0-9a-f]{64})", RegexOptions.None, TimeSpan.FromSeconds(1));
        FidelityAssert.Ensure(match.Success, $"no chunk-signature found to corrupt in {Head(body, 120)}");
        var signature = match.Groups[1].Value;
        var flipped = (signature[0] == '0' ? "1" : "0") + signature[1..];
        return System.Text.Encoding.Latin1.GetBytes(text[..match.Groups[1].Index] + flipped + text[(match.Groups[1].Index + match.Groups[1].Length)..]);
    }
}

/// <summary>Python's <c>b"text" * count</c>.</summary>
public static partial class S3Fidelity
{
    /// <summary><c>b"<paramref name="text"/>" * <paramref name="count"/></c>.</summary>
    public static byte[] RepeatText(string text, int count) =>
        System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(text, count)));
}

/// <summary>What the SigV4 clock-skew ports share.</summary>
public static partial class S3Fidelity
{
    /// <summary>
    /// <c>_server_now(client, bucket)</c>: S3's own current UTC instant, read from the <c>Date</c> header
    /// of a HeadBucket in <paramref name="region"/>.
    /// </summary>
    public static async Task<DateTime> ServerNow(FidelityContext ctx, string region, string bucket)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var (url, host, path) = PathStyleTarget(region, bucket, string.Empty);
        var (_, headers, _) = await Send(ctx, "HEAD", url.TrimEnd('/'), SignHeaders(region, "HEAD", host, path.TrimEnd('/'), [], ctx.Time.GetUtcNow().UtcDateTime));
        FidelityAssert.Ensure(headers.TryGetValue("date", out var date), $"S3 returned no Date header to read its clock from; headers were {Py.Repr(headers.Keys.Order(StringComparer.Ordinal).ToList())}");
        return DateTime.SpecifyKind(DateTimeOffset.Parse(date, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime, DateTimeKind.Utc);
    }
}
