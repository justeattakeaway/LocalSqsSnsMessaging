#pragma warning disable CA1308 // S3 writes metadata names and checksum algorithms in lower case.
using System.Text;
using System.Xml;
using System.Xml.Linq;
using InMemoryAws.S3;

namespace InMemoryAws.Http;

/// <summary>What an S3 request's path addresses: the service (<c>GET /</c>), a bucket or an object.</summary>
internal enum S3PathKind
{
    Service,
    Bucket,
    Object,
}

/// <summary>
/// One S3 request as the REST-XML protocol sees it, whichever host it came through: the method, the
/// bucket and key (from a virtual-hosted host name or the path), the query parameters, the headers and
/// the body, with any <c>aws-chunked</c> encoding already removed.
/// </summary>
internal sealed class S3WireRequest
{
    private readonly List<KeyValuePair<string, string?>> _query;
    private readonly Dictionary<string, string> _headers;

    public S3WireRequest(
        string method,
        string? host,
        string rawPath,
        string? rawQuery,
        IEnumerable<KeyValuePair<string, string>> headers,
        ReadOnlyMemory<byte> body)
    {
        Method = method.ToUpperInvariant();
        Host = host ?? string.Empty;
        RawPath = string.IsNullOrEmpty(rawPath) ? "/" : rawPath;
        RawQuery = rawQuery?.TrimStart('?') ?? string.Empty;
        _query = ParseQuery(RawQuery);
        _headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            _headers[header.Key] = _headers.TryGetValue(header.Key, out var existing) ? existing + "," + header.Value : header.Value;
        }

        Body = body;
        (Bucket, Key, VirtualHosted) = S3Addressing.Parse(Host, RawPath);
        PathKind = Bucket is null ? S3PathKind.Service : Key is null ? S3PathKind.Bucket : S3PathKind.Object;
    }

    /// <summary>The HTTP method, upper case.</summary>
    public string Method { get; }

    /// <summary>The <c>Host</c> the request was sent to, without a port.</summary>
    public string Host { get; }

    /// <summary>The path as sent, still percent-encoded.</summary>
    public string RawPath { get; }

    /// <summary>The query string as sent, without its <c>?</c>.</summary>
    public string RawQuery { get; }

    public S3PathKind PathKind { get; }

    /// <summary>The bucket the request addresses, from the host (virtual-hosted style) or the path.</summary>
    public string? Bucket { get; }

    /// <summary>The object key, percent-decoded; null for a bucket or service request.</summary>
    public string? Key { get; }

    /// <summary>Whether the bucket came from the host name rather than the path.</summary>
    public bool VirtualHosted { get; }

    /// <summary>The body, decoded from <c>aws-chunked</c> when it was sent so.</summary>
    public ReadOnlyMemory<byte> Body { get; private set; }

    /// <summary>The region the request was signed for or sent to, when it names one.</summary>
    public string? Region { get; init; }

    /// <summary>Whether the request came over TLS: S3 refuses an SSE-C key sent over plain HTTP. (Policies see <c>aws:SecureTransport</c> as always true.)</summary>
    public bool SecureTransport { get; init; } = true;

    /// <summary>The operation the router chose; null before routing, or when none matches.</summary>
    public string? Operation { get; set; }

    /// <summary>The query parameters in order, percent-decoded; a parameter with no <c>=</c> has a null value.</summary>
    public IReadOnlyList<KeyValuePair<string, string?>> QueryParameters => _query;

    /// <summary>The headers, by case-insensitive name; repeated headers joined with commas.</summary>
    public IReadOnlyDictionary<string, string> Headers => _headers;

    public bool HasQuery(string name)
    {
        foreach (var parameter in _query)
        {
            if (string.Equals(parameter.Key, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The first value of a query parameter; empty for one sent without <c>=</c>, null when absent.</summary>
    public string? Query(string name)
    {
        foreach (var parameter in _query)
        {
            if (string.Equals(parameter.Key, name, StringComparison.Ordinal))
            {
                return parameter.Value ?? string.Empty;
            }
        }

        return null;
    }

    public string? Header(string name) => _headers.TryGetValue(name, out var value) ? value : null;

    public void SetHeader(string name, string value) => _headers[name] = value;

    public void RemoveHeader(string name) => _headers.Remove(name);

    /// <summary>
    /// The headers that start with <paramref name="prefix"/> (user metadata, <c>x-amz-meta-</c>), keyed
    /// by the rest of the name in lower case, as S3 stores them; null when there are none.
    /// </summary>
    public Dictionary<string, string>? HeadersWithPrefix(string prefix)
    {
        Dictionary<string, string>? values = null;
        foreach (var header in _headers)
        {
            if (header.Key.Length > prefix.Length && header.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                (values ??= new Dictionary<string, string>(StringComparer.Ordinal))[header.Key.Substring(prefix.Length).ToLowerInvariant()] = header.Value;
            }
        }

        return values;
    }

    /// <summary>Replaces the body (with its <c>aws-chunked</c> encoding removed).</summary>
    public void ReplaceBody(ReadOnlyMemory<byte> body) => Body = body;

    /// <summary>The body as UTF-8 text; null when there is none.</summary>
    public string? BodyText() => Body.Length == 0 ? null : Encoding.UTF8.GetString(Body.ToArray());

    /// <summary>The body's root XML element; null when the body is empty. A body that isn't XML is <c>MalformedXML</c>.</summary>
    public XElement? LoadXml()
    {
        if (Body.Length == 0 || IsWhiteSpace(Body.Span))
        {
            return null;
        }

        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var stream = new MemoryStream(Body.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader).Root;
        }
        catch (XmlException)
        {
            throw S3Errors.MalformedXml();
        }
    }

    private static bool IsWhiteSpace(ReadOnlySpan<byte> body)
    {
        foreach (var b in body)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return false;
            }
        }

        return true;
    }

    private static List<KeyValuePair<string, string?>> ParseQuery(string query)
    {
        var parameters = new List<KeyValuePair<string, string?>>();
        if (query.Length == 0)
        {
            return parameters;
        }

        foreach (var part in query.Split('&'))
        {
            if (part.Length == 0)
            {
                continue;
            }

            var eq = part.IndexOf('=', StringComparison.Ordinal);
            parameters.Add(eq < 0
                ? new KeyValuePair<string, string?>(DecodeQuery(part), null)
                : new KeyValuePair<string, string?>(DecodeQuery(part.Substring(0, eq)), DecodeQuery(part.Substring(eq + 1))));
        }

        return parameters;
    }

    private static string DecodeQuery(string value) => S3Addressing.PercentDecode(value.Replace('+', ' '));
}

/// <summary>Where an S3 request names its bucket and key: the host (virtual-hosted style) or the path.</summary>
internal static class S3Addressing
{
    /// <summary>
    /// The bucket and key of a request. A host such as <c>my-bucket.s3.us-west-2.amazonaws.com</c> (or
    /// any host with an <c>s3</c> label after the first, such as <c>my-bucket.s3.localhost</c>) names the
    /// bucket, and the whole path is the key; otherwise the first path segment is the bucket.
    /// </summary>
    public static (string? Bucket, string? Key, bool VirtualHosted) Parse(string host, string rawPath)
    {
        var path = rawPath.Length > 0 && rawPath[0] == '/' ? rawPath.Substring(1) : rawPath;
        if (BucketFromHost(host) is { } hostBucket)
        {
            return (hostBucket, path.Length == 0 ? null : PercentDecode(path), true);
        }

        if (path.Length == 0)
        {
            return (null, null, false);
        }

        var slash = path.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return (PercentDecode(path), null, false);
        }

        var bucket = PercentDecode(path.Substring(0, slash));
        var key = path.Substring(slash + 1);
        return (bucket, key.Length == 0 ? null : PercentDecode(key), false);
    }

    private static string? BucketFromHost(string host)
    {
        if (host.Length == 0)
        {
            return null;
        }

        var colon = host.LastIndexOf(':');
        if (colon > 0 && host.IndexOf(']', StringComparison.Ordinal) < colon)
        {
            host = host.Substring(0, colon);
        }

        // s3.amazonaws.com, s3.us-west-2.amazonaws.com, s3-us-west-2.amazonaws.com, an IP address or
        // localhost: path style.
        var labels = host.Split('.');
        for (var i = 1; i < labels.Length; i++)
        {
            // S3 Express: a directory bucket's zonal endpoint is bucket.s3express-usw2-az1.us-west-2.amazonaws.com.
            if (labels[i] == "s3" || labels[i].StartsWith("s3-", StringComparison.Ordinal) || labels[i].StartsWith("s3express-", StringComparison.Ordinal))
            {
                return labels[i] == "s3express-control" ? null : string.Join(".", labels, 0, i);
            }
        }

        return null;
    }

    /// <summary>Decodes <c>%XX</c> escapes as UTF-8, leaving anything else (a <c>+</c> included) as it is.</summary>
    public static string PercentDecode(string value)
    {
        if (value.IndexOf('%', StringComparison.Ordinal) < 0)
        {
            return value;
        }

        var bytes = new List<byte>(value.Length);
        var literalStart = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length && IsHex(value[i + 1]) && IsHex(value[i + 2]))
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(value.Substring(literalStart, i - literalStart)));
                bytes.Add((byte)((HexValue(value[i + 1]) << 4) | HexValue(value[i + 2])));
                i += 2;
                literalStart = i + 1;
            }
        }

        bytes.AddRange(Encoding.UTF8.GetBytes(value.Substring(literalStart)));

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static bool IsHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    private static int HexValue(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
}
