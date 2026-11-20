using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using InMemoryAws.Http;

namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>
/// What the S3 signing, presigned-URL, ACL and streaming-upload ports share: the checks' credentials as
/// raw key material, a hand-rolled SigV4 signer (headers and presigned query strings, at an explicit
/// instant), the <c>aws-chunked</c> bodies the streaming checks build, and raw HTTP to the cloud.
/// </summary>
public static partial class S3Fidelity
{
    /// <summary><c>_signing_creds(ctx)["access_key"]</c>: the access key every check's S3 client signs with.</summary>
    public const string AccessKey = "test";

    /// <summary><c>_signing_creds(ctx)["secret_key"]</c>.</summary>
    public const string SecretKey = "test";

    /// <summary>The SHA-256 of nothing, as SigV4 writes it.</summary>
    public const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>S3's <c>SignatureDoesNotMatch</c> message.</summary>
    public const string SignatureMismatchMessage =
        "The request signature we calculated does not match the signature you provided. Check your key and signing method.";

    /// <summary><c>hashlib.sha256(data).hexdigest()</c>.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>The SigV4 signing key for a date, region and service.</summary>
    public static byte[] SigningKey(string secret, string date, string region, string service = "s3")
    {
        var key = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + secret), Encoding.UTF8.GetBytes(date));
        key = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(region));
        key = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(service));
        return HMACSHA256.HashData(key, "aws4_request"u8);
    }

    /// <summary>The hex HMAC-SHA256 of <paramref name="text"/> under <paramref name="key"/>.</summary>
    public static string HmacHex(byte[] key, string text) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text)));

    /// <summary><c>urllib.parse.quote(value, safe=...)</c>: everything but unreserved characters (and <paramref name="safe"/>) percent-encoded.</summary>
    public static string Quote(string value, string safe = "/")
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '~' || (b < 128 && safe.Contains(c, StringComparison.Ordinal)))
            {
                text.Append(c);
            }
            else
            {
                text.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return text.ToString();
    }

    /// <summary>An instant as SigV4's <c>X-Amz-Date</c> writes it (<c>%Y%m%dT%H%M%SZ</c>).</summary>
    public static string AmzDate(DateTime when) => when.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary><c>_amz_instant(value)</c>: an <c>X-Amz-Date</c> as a UTC instant.</summary>
    public static DateTime AmzInstant(string value) =>
        DateTime.ParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    /// <summary>An instant as S3's error documents write it (<c>%Y-%m-%dT%H:%M:%SZ</c>).</summary>
    public static string IsoSeconds(DateTime when) => when.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>_sigv4_headers(creds, region, method, host, path, payload, when, extra)</c>: the headers of an
    /// S3 REST request SigV4-signed at an explicit instant, <c>authorization</c> included.
    /// </summary>
    public static Dictionary<string, string> SignHeaders(
        string region,
        string method,
        string host,
        string path,
        byte[] payload,
        DateTime when,
        IReadOnlyDictionary<string, string>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return SignWithPayloadHash(region, method, host, path, Sha256Hex(payload), when, extra).Headers;
    }

    /// <summary>
    /// <c>_seed_sign(...)</c>: SigV4-signs a request's headers with <paramref name="payloadHash"/> (a
    /// payload hash or a streaming sentinel) as <c>x-amz-content-sha256</c>; returns the headers, the
    /// signature and the credential scope.
    /// </summary>
    public static (Dictionary<string, string> Headers, string Signature, string Scope) SignWithPayloadHash(
        string region,
        string method,
        string host,
        string path,
        string payloadHash,
        DateTime when,
        IReadOnlyDictionary<string, string>? extra = null)
    {
        var amzDate = AmzDate(when);
        var datestamp = amzDate[..8];
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate,
        };
        foreach (var (name, value) in extra ?? new Dictionary<string, string>())
        {
            headers[name.ToLowerInvariant()] = value;
        }

        var names = headers.Keys.Order(StringComparer.Ordinal).ToList();
        var canonicalHeaders = string.Concat(names.Select(n => $"{n}:{headers[n].Trim()}\n"));
        var signedHeaders = string.Join(";", names);
        var canonicalRequest = string.Join("\n", method, Quote(path, "/~"), string.Empty, canonicalHeaders, signedHeaders, payloadHash);
        var scope = $"{datestamp}/{region}/s3/aws4_request";
        var stringToSign = string.Join("\n", "AWS4-HMAC-SHA256", amzDate, scope, Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest)));
        var signature = HmacHex(SigningKey(SecretKey, datestamp, region), stringToSign);
        headers["authorization"] =
            $"AWS4-HMAC-SHA256 Credential={AccessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
        return (headers, signature, scope);
    }

    /// <summary>
    /// <c>_chunk_signature(...)</c>: the next link in a streaming upload's chunk-signature chain, a
    /// function of the previous signature and this chunk's bytes.
    /// </summary>
    public static string ChunkSignature(string region, string previous, string amzDate, string scope, ReadOnlySpan<byte> chunk)
    {
        var stringToSign = string.Join("\n", "AWS4-HMAC-SHA256-PAYLOAD", amzDate, scope, previous, EmptySha256, Sha256Hex(chunk));
        return HmacHex(SigningKey(SecretKey, amzDate[..8], region), stringToSign);
    }

    /// <summary>
    /// <c>_chunk_body_signed(...)</c>: the <c>aws-chunked</c> body of a
    /// <c>STREAMING-AWS4-HMAC-SHA256-PAYLOAD</c> upload, each chunk and the final empty one signed in a chain.
    /// </summary>
    public static byte[] SignedChunkBody(string region, IReadOnlyList<byte[]> chunks, string amzDate, string scope, string seedSignature)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        using var body = new MemoryStream();
        var previous = seedSignature;
        foreach (var chunk in chunks)
        {
            previous = ChunkSignature(region, previous, amzDate, scope, chunk);
            body.Write(Encoding.ASCII.GetBytes($"{chunk.Length:x};chunk-signature={previous}\r\n"));
            body.Write(chunk);
            body.Write("\r\n"u8);
        }

        previous = ChunkSignature(region, previous, amzDate, scope, []);
        body.Write(Encoding.ASCII.GetBytes($"0;chunk-signature={previous}\r\n\r\n"));
        return body.ToArray();
    }

    /// <summary>
    /// <c>_chunk_body_unsigned_trailer(chunks, name, value)</c>: the <c>aws-chunked</c> body of a
    /// <c>STREAMING-UNSIGNED-PAYLOAD-TRAILER</c> upload, ending with one trailing header.
    /// </summary>
    public static byte[] UnsignedTrailerBody(IReadOnlyList<byte[]> chunks, string trailerName, string trailerValue)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        using var body = new MemoryStream();
        foreach (var chunk in chunks)
        {
            body.Write(Encoding.ASCII.GetBytes($"{chunk.Length:x}\r\n"));
            body.Write(chunk);
            body.Write("\r\n"u8);
        }

        body.Write("0\r\n"u8);
        body.Write(Encoding.ASCII.GetBytes($"{trailerName}:{trailerValue}\r\n\r\n"));
        return body.ToArray();
    }

    /// <summary><c>payload</c> cut into chunks of <paramref name="size"/> bytes (one empty chunk for an empty payload).</summary>
    public static List<byte[]> Chunks(byte[] payload, int size = 64 * 1024)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var chunks = new List<byte[]>();
        for (var i = 0; i < payload.Length; i += size)
        {
            chunks.Add(payload[i..Math.Min(payload.Length, i + size)]);
        }

        return chunks.Count == 0 ? [[]] : chunks;
    }

    /// <summary>
    /// <c>_target(client, bucket, key)</c>: the URL, host and path of a raw path-style request on a
    /// client's regional endpoint.
    /// </summary>
    public static (string Url, string Host, string Path) PathStyleTarget(string region, string bucket, string key)
    {
        var host = $"s3.{region}.amazonaws.com";
        var path = $"/{bucket}/{key}";
        return ($"https://{host}{path}", host, path);
    }

    /// <summary>
    /// <c>generate_presigned_url(...)</c> from a SigV4 (<c>s3v4</c>) client: the URL of a request
    /// presigned for <paramref name="expiresIn"/> seconds at the cloud's current time (the client's clock
    /// in step with S3's). <paramref name="signedHeaders"/> are headers the request must send, bound into
    /// the signature; <paramref name="query"/> are the operation's own query parameters.
    /// </summary>
    public static string Presign(
        FidelityContext ctx,
        string method,
        string bucket,
        string? key,
        int expiresIn,
        IReadOnlyDictionary<string, string>? signedHeaders = null,
        IReadOnlyList<KeyValuePair<string, string>>? query = null,
        bool pathStyle = false,
        string? region = null,
        DateTime? at = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        region ??= ctx.Region;
        var when = at ?? ctx.Time.GetUtcNow().UtcDateTime;
        var amzDate = AmzDate(when);
        var scope = $"{amzDate[..8]}/{region}/s3/aws4_request";
        var host = pathStyle ? $"s3.{region}.amazonaws.com" : $"{bucket}.s3.{region}.amazonaws.com";
        var path = (pathStyle ? $"/{bucket}" : string.Empty) + "/" + (key is null ? string.Empty : Quote(key, "/~"));
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["host"] = host };
        foreach (var (name, value) in signedHeaders ?? new Dictionary<string, string>())
        {
            headers[name.ToLowerInvariant()] = value;
        }

        var parameters = new List<KeyValuePair<string, string>>(query ?? [])
        {
            new("X-Amz-Algorithm", "AWS4-HMAC-SHA256"),
            new("X-Amz-Credential", $"{AccessKey}/{scope}"),
            new("X-Amz-Date", amzDate),
            new("X-Amz-Expires", expiresIn.ToString(CultureInfo.InvariantCulture)),
            new("X-Amz-SignedHeaders", string.Join(";", headers.Keys)),
        };
        var canonicalQuery = string.Join(
            "&",
            parameters.Select(p => (Name: Quote(p.Key, "~"), Value: Quote(p.Value, "~")))
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ThenBy(p => p.Value, StringComparer.Ordinal)
                .Select(p => $"{p.Name}={p.Value}"));
        var canonicalHeaders = string.Concat(headers.Select(h => $"{h.Key}:{h.Value.Trim()}\n"));
        var canonicalRequest = string.Join("\n", method, path, canonicalQuery, canonicalHeaders, string.Join(";", headers.Keys), "UNSIGNED-PAYLOAD");
        var stringToSign = string.Join("\n", "AWS4-HMAC-SHA256", amzDate, scope, Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest)));
        var signature = HmacHex(SigningKey(SecretKey, amzDate[..8], region), stringToSign);
        parameters.Add(new("X-Amz-Signature", signature));
        return $"https://{host}{path}?" + string.Join("&", parameters.Select(p => $"{Quote(p.Key, "~")}={Quote(p.Value, "~")}"));
    }

    /// <summary><c>_signed_query(url)</c>: a URL's query as an ordered list of decoded (name, value) pairs.</summary>
    public static List<KeyValuePair<string, string>> SignedQuery(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var query = url.Contains('?', StringComparison.Ordinal) ? url[(url.IndexOf('?', StringComparison.Ordinal) + 1)..] : string.Empty;
        return query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                var eq = part.IndexOf('=', StringComparison.Ordinal);
                return eq < 0
                    ? new KeyValuePair<string, string>(Uri.UnescapeDataString(part), string.Empty)
                    : new KeyValuePair<string, string>(Uri.UnescapeDataString(part[..eq].Replace('+', ' ')), Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' ')));
            })
            .ToList();
    }

    /// <summary><c>_rebuild(url, pairs)</c>: <paramref name="url"/> with <paramref name="pairs"/> as its query, encoded as SigV4 encodes.</summary>
    public static string Rebuild(string url, IEnumerable<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(url);
        var question = url.IndexOf('?', StringComparison.Ordinal);
        var bare = question < 0 ? url : url[..question];
        return bare + "?" + string.Join("&", pairs.Select(p => $"{Quote(p.Key, "~")}={Quote(p.Value, "~")}"));
    }

    /// <summary><c>_signed_headers(url)</c>: the header names a presigned URL's <c>X-Amz-SignedHeaders</c> binds.</summary>
    public static HashSet<string> SignedHeaderNames(string url) =>
        [.. (SignedQuery(url).FirstOrDefault(p => p.Key == "X-Amz-SignedHeaders").Value ?? string.Empty).Split(';')];

    /// <summary><c>_as_http(url)</c>: the same URL over plain HTTP (the signature does not cover the scheme).</summary>
    public static string AsHttp(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        FidelityAssert.Ensure(url.StartsWith("https://", StringComparison.Ordinal), $"expected an https URL to downgrade, got {Py.Repr(url.Split('?')[0])}");
        return "http://" + url["https://".Length..];
    }

    /// <summary><c>urlsplit(url)._replace(query="").geturl()</c>.</summary>
    public static string WithoutQuery(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var question = url.IndexOf('?', StringComparison.Ordinal);
        return question < 0 ? url : url[..question];
    }

    /// <summary>
    /// A raw HTTP request to the cloud (<c>urllib.request</c>, never following a redirect): returns the
    /// status, the headers with lower-case names, and the body. As urllib does, a request with a body and
    /// no <c>Content-Type</c> is sent as <c>application/x-www-form-urlencoded</c>.
    /// </summary>
    public static async Task<(int Status, Dictionary<string, string> Headers, byte[] Body)> Send(
        FidelityContext ctx,
        string method,
        string url,
        IReadOnlyDictionary<string, string>? headers = null,
        byte[]? data = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var http = ctx.RawHttpClient(AwsServiceType.S3);
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(url));
        if (data is not null)
        {
            request.Content = new ByteArrayContent(data);
        }

        var contentTypeSet = false;
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            if (string.Equals(name, "host", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Host = value;
            }
            else if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content ??= new ByteArrayContent([]);
                if (string.Equals(name, "content-length", StringComparison.OrdinalIgnoreCase))
                {
                    request.Content.Headers.ContentLength = long.Parse(value, CultureInfo.InvariantCulture);
                }
                else
                {
                    request.Content.Headers.Remove(name);
                    request.Content.Headers.TryAddWithoutValidation(name, value);
                }

                contentTypeSet |= string.Equals(name, "content-type", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (data is not null && !contentTypeSet)
        {
            request.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        }

        using var response = await http.SendAsync(request);
        var answered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            answered[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
        }

        return ((int)response.StatusCode, answered, await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary><c>_xml_error(body)</c>: the (Code, Message) of an S3 XML error body; nulls when it is not one.</summary>
    public static (string? Code, string? Message) XmlCodeMessage(byte[] body)
    {
        var fields = XmlFields(body);
        return (fields?.GetValueOrDefault("Code"), fields?.GetValueOrDefault("Message"));
    }

    /// <summary><c>_error_xml(body)</c>: an S3 error document's elements, by name; null when the body is not XML.</summary>
    public static Dictionary<string, string>? XmlFields(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        try
        {
            var root = XElement.Parse(Encoding.UTF8.GetString(body));
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var child in root.Elements())
            {
                fields[child.Name.LocalName] = child.Value;
            }

            return fields;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>Python's <c>(code, message) == (expected, ...)</c> on an XML error body.</summary>
    public static string ReprXmlError(byte[] body) => Repr(XmlCodeMessage(body));
}
