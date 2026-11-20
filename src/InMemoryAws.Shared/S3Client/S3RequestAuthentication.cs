using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using InMemoryAws.Http;

namespace InMemoryAws.S3;

/// <summary>
/// What S3 checks about a request before it looks at what the request asks for: its transport and
/// streaming headers, and, for a request signed by an access key whose secret the cloud knows
/// (<see cref="InMemoryAwsCloud.SecretAccessKeys"/>), its SigV4 signature, its time, a presigned
/// URL's expiry, and each chunk signature of a streamed upload.
/// </summary>
/// <remarks>
/// Runs before the <c>aws-chunked</c> body is decoded, on the request exactly as it was sent. A request
/// signed by an access key the cloud doesn't know, or not signed at all, is served without these
/// checks, so tests and applications can sign with any credentials. The trailer signature of a signed
/// streaming upload (<c>STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER</c>) is not checked.
/// </remarks>
internal static class S3RequestAuthentication
{
    /// <summary>How far a request's time may be from the cloud's clock: 15 minutes, as S3 allows.</summary>
    public const int MaxAllowedSkewMilliseconds = 900_000;

    /// <summary>The longest a presigned URL may last: one week.</summary>
    public const int MaxPresignedExpirySeconds = 604_800;

    private const string Algorithm = "AWS4-HMAC-SHA256";

    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private const string SignatureMismatchMessage =
        "The request signature we calculated does not match the signature you provided. Check your key and signing method.";

    /// <summary>Checks <paramref name="request"/>; refuses it with the <see cref="S3Exception"/> S3 answers.</summary>
    public static void Check(S3WireRequest request, InMemoryAwsCloud cloud)
    {
        CheckSseCustomerTransport(request);
        CheckStreamingHeaders(request);
        var now = cloud.TimeProvider.GetUtcNow().UtcDateTime;
        if (request.Query("X-Amz-Algorithm") is not null || request.Query("X-Amz-Credential") is not null)
        {
            CheckPresigned(request, cloud, now);
        }
        else if (request.Header("Authorization") is { } authorization
            && authorization.StartsWith(Algorithm + " ", StringComparison.Ordinal))
        {
            CheckAuthorizationHeader(request, cloud, authorization, now);
        }
    }

    /// <summary>The key SigV4 signs with: HMACs of the date, region and service, keyed by the secret.</summary>
    public static byte[] SigningKey(string secret, string date, string region, string service)
    {
        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + secret), date);
        key = Hmac(key, region);
        key = Hmac(key, service);
        return Hmac(key, "aws4_request");
    }

    /// <summary>HMAC-SHA256 of <paramref name="text"/>'s UTF-8 bytes.</summary>
    public static byte[] Hmac(byte[] key, string text)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// The secret of the access key a SigV4 credential (<c>AKID/date/region/service/aws4_request</c>)
    /// names, and the credential's parts; null when the credential is malformed or the cloud doesn't
    /// know the key.
    /// </summary>
    public static (string Secret, string AccessKey, string Date, string Region, string Service)? KnownCredential(InMemoryAwsCloud cloud, string? credential)
    {
        var parts = credential?.Split('/');
        if (parts is not { Length: 5 } || parts[4] != "aws4_request"
            || cloud.SecretAccessKeys is not { } secrets || !secrets.TryGetValue(parts[0], out var secret))
        {
            return null;
        }

        return (secret, parts[0], parts[1], parts[2], parts[3]);
    }

    /// <summary>
    /// SSE-C sends the customer's key, so S3 refuses any request carrying one over plain HTTP, whatever
    /// the bucket's policy says.
    /// </summary>
    private static void CheckSseCustomerTransport(S3WireRequest request)
    {
        if (!request.SecureTransport
            && (request.Header("x-amz-server-side-encryption-customer-algorithm") is not null
                || request.Header("x-amz-server-side-encryption-customer-key") is not null
                || request.Header("x-amz-copy-source-server-side-encryption-customer-algorithm") is not null))
        {
            throw new S3Exception(
                "InvalidArgument",
                "Requests specifying Server Side Encryption with Customer provided keys must be made over a secure connection.",
                HttpStatusCode.BadRequest);
        }
    }

    /// <summary>
    /// A streamed (<c>aws-chunked</c>) upload must say how long its decoded body is, and its trailing
    /// headers must be the ones <c>x-amz-trailer</c> declared.
    /// </summary>
    private static void CheckStreamingHeaders(S3WireRequest request)
    {
        var sha = request.Header("x-amz-content-sha256");
        if (sha is null || !sha.StartsWith("STREAMING-", StringComparison.Ordinal) || request.Body.Length == 0)
        {
            return;
        }

        if (request.Header("x-amz-decoded-content-length") is null)
        {
            throw new S3Exception("MissingContentLength", "You must provide the Content-Length HTTP header.", HttpStatusCode.LengthRequired);
        }

        if (request.Header("x-amz-trailer") is not { } declared || ParseChunks(request.Body.Span) is not { } body)
        {
            return;
        }

        var allowed = new HashSet<string>(declared.Split(',').Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase)
        {
            "x-amz-trailer-signature",
        };
        if (body.Trailers.Exists(t => !allowed.Contains(t)))
        {
            throw new S3Exception(
                "MalformedTrailerError",
                "The request contained trailing data that was not well-formed or did not conform to our published schema.",
                HttpStatusCode.BadRequest);
        }
    }

    /// <summary>
    /// A request signed in its <c>Authorization</c> header: its time must be within 15 minutes of the
    /// cloud's clock, its signature must match, and so must a hashed payload and each chunk signature.
    /// </summary>
    private static void CheckAuthorizationHeader(S3WireRequest request, InMemoryAwsCloud cloud, string authorization, DateTime now)
    {
        string? credential = null, signedHeaders = null, signature = null;
        foreach (var part in authorization.Substring(Algorithm.Length + 1).Split(','))
        {
            var pair = part.Trim();
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var value = pair.Substring(eq + 1).Trim();
            switch (pair.Substring(0, eq).Trim())
            {
                case "Credential":
                    credential = value;
                    break;
                case "SignedHeaders":
                    signedHeaders = value;
                    break;
                case "Signature":
                    signature = value;
                    break;
            }
        }

        if (KnownCredential(cloud, credential) is not { } known || signedHeaders is null || signature is null)
        {
            return;
        }

        var amzDate = request.Header("x-amz-date");
        if (amzDate is null || !TryParseAmzDate(amzDate, out var requestTime))
        {
            return;
        }

        if (Math.Abs((requestTime - now).TotalMilliseconds) > MaxAllowedSkewMilliseconds)
        {
            throw new S3Exception(
                "RequestTimeTooSkewed",
                "The difference between the request time and the current time is too large.",
                HttpStatusCode.Forbidden,
                ("RequestTime", amzDate),
                ("ServerTime", FormatIso(now)),
                ("MaxAllowedSkewMilliseconds", MaxAllowedSkewMilliseconds.ToString(CultureInfo.InvariantCulture)));
        }

        var payloadHash = request.Header("x-amz-content-sha256") ?? HexSha256(request.Body.Span);
        var scope = $"{known.Date}/{known.Region}/{known.Service}/aws4_request";
        var key = SigningKey(known.Secret, known.Date, known.Region, known.Service);
        VerifySignature(request, known.AccessKey, key, amzDate, scope, signedHeaders, signature, payloadHash, presigned: false);

        if (payloadHash.Length == 64 && IsHex(payloadHash))
        {
            var computed = HexSha256(request.Body.Span);
            if (!string.Equals(computed, payloadHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new S3Exception(
                    "XAmzContentSHA256Mismatch",
                    "The provided 'x-amz-content-sha256' header does not match what was computed.",
                    HttpStatusCode.BadRequest,
                    ("ClientComputedContentSHA256", payloadHash),
                    ("S3ComputedContentSHA256", computed));
            }
        }
        else if (payloadHash.StartsWith("STREAMING-AWS4-HMAC-SHA256-PAYLOAD", StringComparison.Ordinal))
        {
            VerifyChunkSignatures(request, known.AccessKey, key, amzDate, scope, signature);
        }
    }

    /// <summary>
    /// A presigned URL: its expiry may be at most a week; it must not have expired on the cloud's
    /// clock; and its signature must match.
    /// </summary>
    private static void CheckPresigned(S3WireRequest request, InMemoryAwsCloud cloud, DateTime now)
    {
        var expiresText = request.Query("X-Amz-Expires");
        int? expires = int.TryParse(expiresText, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
        if (expires > MaxPresignedExpirySeconds)
        {
            throw new S3Exception(
                "AuthorizationQueryParametersError",
                $"X-Amz-Expires must be less than a week (in seconds); that is, the given X-Amz-Expires must be less than {MaxPresignedExpirySeconds} seconds",
                HttpStatusCode.BadRequest);
        }

        if (request.Query("X-Amz-Algorithm") != Algorithm
            || KnownCredential(cloud, request.Query("X-Amz-Credential")) is not { } known
            || request.Query("X-Amz-Date") is not { } amzDate
            || !TryParseAmzDate(amzDate, out var signedAt)
            || request.Query("X-Amz-SignedHeaders") is not { } signedHeaders
            || request.Query("X-Amz-Signature") is not { } signature
            || expires is not { } lifetime)
        {
            return;
        }

        var expiry = signedAt.AddSeconds(lifetime);
        if (now > expiry)
        {
            throw new S3Exception(
                "AccessDenied",
                "Request has expired",
                HttpStatusCode.Forbidden,
                ("X-Amz-Expires", expiresText),
                ("Expires", FormatIso(expiry)),
                ("ServerTime", FormatIso(now)));
        }

        var scope = $"{known.Date}/{known.Region}/{known.Service}/aws4_request";
        var key = SigningKey(known.Secret, known.Date, known.Region, known.Service);
        var payloadHash = request.Query("X-Amz-Content-Sha256") ?? "UNSIGNED-PAYLOAD";
        VerifySignature(request, known.AccessKey, key, amzDate, scope, signedHeaders, signature, payloadHash, presigned: true);
    }

    /// <summary>
    /// Recomputes the request's signature and refuses a mismatch with <c>SignatureDoesNotMatch</c>,
    /// which names the string to sign and the canonical request S3 computed. The path is tried as sent
    /// and as SigV4 encodes it, since clients differ in what they leave unescaped.
    /// </summary>
    private static void VerifySignature(
        S3WireRequest request,
        string accessKey,
        byte[] signingKey,
        string amzDate,
        string scope,
        string signedHeaders,
        string signature,
        string payloadHash,
        bool presigned)
    {
        var query = CanonicalQuery(request.RawQuery, presigned);
        var headers = CanonicalHeaders(request, signedHeaders);
        string? lastCanonical = null, lastStringToSign = null;
        foreach (var path in new[] { UriEncode(S3Addressing.PercentDecode(request.RawPath), keepSlash: true), request.RawPath }.Distinct(StringComparer.Ordinal))
        {
            var canonical = string.Join("\n", request.Method, path, query, headers, signedHeaders, payloadHash);
            var stringToSign = string.Join("\n", Algorithm, amzDate, scope, HexSha256(Encoding.UTF8.GetBytes(canonical)));
            if (string.Equals(Hex(Hmac(signingKey, stringToSign)), signature, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lastCanonical ??= canonical;
            lastStringToSign ??= stringToSign;
        }

        throw SignatureDoesNotMatch(accessKey, lastStringToSign!, signature, lastCanonical);
    }

    /// <summary>
    /// Follows the chain of chunk signatures of a <c>STREAMING-AWS4-HMAC-SHA256-PAYLOAD</c> body, each
    /// signing the previous signature and its own chunk's bytes, from the seed signature of the
    /// headers to the final zero-length chunk.
    /// </summary>
    private static void VerifyChunkSignatures(S3WireRequest request, string accessKey, byte[] signingKey, string amzDate, string scope, string seedSignature)
    {
        var body = request.Body.Span;
        if (ParseChunks(body) is not { } parsed)
        {
            return;
        }

        var previous = seedSignature;
        foreach (var chunk in parsed.Chunks)
        {
            var stringToSign = string.Join(
                "\n",
                "AWS4-HMAC-SHA256-PAYLOAD",
                amzDate,
                scope,
                previous,
                EmptySha256,
                HexSha256(body.Slice(chunk.Offset, chunk.Length)));
            var expected = Hex(Hmac(signingKey, stringToSign));
            if (chunk.Signature is null || !string.Equals(expected, chunk.Signature, StringComparison.OrdinalIgnoreCase))
            {
                throw SignatureDoesNotMatch(accessKey, stringToSign, chunk.Signature ?? string.Empty, null);
            }

            previous = expected;
        }
    }

    private static S3Exception SignatureDoesNotMatch(string accessKey, string stringToSign, string signature, string? canonicalRequest)
    {
        var details = new List<(string, string?)>
        {
            ("AWSAccessKeyId", accessKey),
            ("StringToSign", stringToSign),
            ("SignatureProvided", signature),
            ("StringToSignBytes", HexBytes(stringToSign)),
        };
        if (canonicalRequest is not null)
        {
            details.Add(("CanonicalRequest", canonicalRequest));
            details.Add(("CanonicalRequestBytes", HexBytes(canonicalRequest)));
        }

        return new S3Exception("SignatureDoesNotMatch", SignatureMismatchMessage, HttpStatusCode.Forbidden, [.. details]);
    }

    /// <summary>
    /// The query in SigV4's canonical form: every parameter (but a presigned URL's signature) encoded,
    /// sorted by name and then value, a parameter without a value given an empty one.
    /// </summary>
    private static string CanonicalQuery(string rawQuery, bool presigned)
    {
        var pairs = new List<(string Name, string Value)>();
        foreach (var part in rawQuery.Split('&'))
        {
            if (part.Length == 0)
            {
                continue;
            }

            var eq = part.IndexOf('=', StringComparison.Ordinal);
            var name = S3Addressing.PercentDecode(eq < 0 ? part : part.Substring(0, eq));
            var value = eq < 0 ? string.Empty : S3Addressing.PercentDecode(part.Substring(eq + 1));
            if (presigned && name == "X-Amz-Signature")
            {
                continue;
            }

            pairs.Add((UriEncode(name, keepSlash: false), UriEncode(value, keepSlash: false)));
        }

        return string.Join(
            "&",
            pairs.OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Value, StringComparer.Ordinal).Select(p => p.Name + "=" + p.Value));
    }

    /// <summary>The signed headers in SigV4's canonical form: <c>name:value</c> lines, values trimmed and spaces collapsed.</summary>
    private static string CanonicalHeaders(S3WireRequest request, string signedHeaders)
    {
        var text = new StringBuilder();
        foreach (var name in signedHeaders.Split(';'))
        {
            var value = request.Header(name) ?? (name == "host" ? request.Host : string.Empty);
            text.Append(name).Append(':').Append(CollapseSpaces(value.Trim())).Append('\n');
        }

        return text.ToString();
    }

    private static string CollapseSpaces(string value)
    {
        if (value.IndexOf("  ", StringComparison.Ordinal) < 0)
        {
            return value;
        }

        var text = new StringBuilder(value.Length);
        var space = false;
        foreach (var c in value)
        {
            if (c == ' ')
            {
                if (!space)
                {
                    text.Append(c);
                }

                space = true;
            }
            else
            {
                text.Append(c);
                space = false;
            }
        }

        return text.ToString();
    }

    /// <summary>SigV4's URI encoding: unreserved characters stay, everything else is <c>%XX</c> of its UTF-8 bytes.</summary>
    private static string UriEncode(string value, bool keepSlash)
    {
        var text = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '~' || (keepSlash && c == '/'))
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

    /// <summary>One chunk of an <c>aws-chunked</c> body: where its data is, and its signature, if any.</summary>
    private readonly record struct Chunk(int Offset, int Length, string? Signature);

    /// <summary>The chunks of an <c>aws-chunked</c> body (the final zero-length one included) and the names of its trailing headers; null when it is malformed.</summary>
    private static (List<Chunk> Chunks, List<string> Trailers)? ParseChunks(ReadOnlySpan<byte> body)
    {
        var chunks = new List<Chunk>();
        var position = 0;
        while (true)
        {
            var lineEnd = IndexOfCrLf(body, position);
            if (lineEnd < 0)
            {
                return null;
            }

            var header = Encoding.ASCII.GetString(body.Slice(position, lineEnd - position).ToArray());
            var semicolon = header.IndexOf(';', StringComparison.Ordinal);
            var sizeText = (semicolon < 0 ? header : header.Substring(0, semicolon)).Trim();
            if (!int.TryParse(sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size) || size < 0)
            {
                return null;
            }

            string? signature = null;
            if (semicolon >= 0)
            {
                var extension = header.Substring(semicolon + 1).Trim();
                const string prefix = "chunk-signature=";
                if (extension.StartsWith(prefix, StringComparison.Ordinal))
                {
                    signature = extension.Substring(prefix.Length);
                }
            }

            position = lineEnd + 2;
            if (position + size > body.Length)
            {
                return null;
            }

            chunks.Add(new Chunk(position, size, signature));
            if (size == 0)
            {
                break;
            }

            position += size;
            if (position + 2 <= body.Length && body[position] == '\r' && body[position + 1] == '\n')
            {
                position += 2;
            }
        }

        var trailers = new List<string>();
        while (position < body.Length)
        {
            var lineEnd = IndexOfCrLf(body, position);
            var line = Encoding.ASCII.GetString(body.Slice(position, (lineEnd < 0 ? body.Length : lineEnd) - position).ToArray());
            position = lineEnd < 0 ? body.Length : lineEnd + 2;
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                trailers.Add(line.Substring(0, colon).Trim());
            }
        }

        return (chunks, trailers);
    }

    private static int IndexOfCrLf(ReadOnlySpan<byte> body, int start)
    {
        for (var i = start; i + 1 < body.Length; i++)
        {
            if (body[i] == '\r' && body[i + 1] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Parses SigV4's <c>yyyyMMddTHHmmssZ</c>.</summary>
    public static bool TryParseAmzDate(string text, out DateTime value) =>
        DateTime.TryParseExact(text, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value);

    private static string FormatIso(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string HexSha256(ReadOnlySpan<byte> data) => Hex(SHA256.HashData(data.ToArray()));

    private static string Hex(byte[] bytes) => S3Checksums.Hex(bytes);

    private static string HexBytes(string text) =>
        string.Join(" ", Encoding.UTF8.GetBytes(text).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));

    private static bool IsHex(string text) => text.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));
}
