using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using InMemoryAws.S3;

namespace InMemoryAws.Http;

/// <summary>
/// S3 over its REST-XML protocol: the method, the path (bucket and key) and the query string name the
/// operation, and a request's members come from the path, the query, headers and the body.
/// Transport-neutral, so the library's HTTP handler and the server answer alike. The generated half
/// (<c>S3WireProtocol.g.cs</c>) routes and binds; this half decodes the body, answers errors as S3's
/// XML error document, and adds the headers every S3 response carries.
/// </summary>
internal static partial class S3WireProtocol
{
    private static readonly RandomNumberGenerator Random = RandomNumberGenerator.Create();

    /// <summary>
    /// Serves one request, against <paramref name="cloud"/>. Only cancellation escapes: every failure
    /// is answered with S3's error document (or, for a HEAD request, the error's status alone).
    /// </summary>
    public static async ValueTask<S3WireResponse> HandleAsync(S3WireRequest request, InMemoryAwsCloud cloud, CancellationToken cancellationToken)
    {
        if (InternalS3Client.ServeWebsite(request, cloud) is { } website)
        {
            return website;
        }

        var response = new S3WireResponse();
        var requestId = NewRequestId();
        string? operation = null;
#if NET
        System.Diagnostics.Activity? activity = null;
#endif
        Exception? failure = null;
        try
        {
            S3RequestAuthentication.Check(request, cloud);
            S3ChunkedBody.Decode(request);

            // S3's front end refuses a NUL in an object key before any operation sees it: a bare 400,
            // with no error document.
            if (request.Key is { } key && key.Contains('\0', StringComparison.Ordinal))
            {
                throw new S3Exception(BareRefusal, "A NUL in an object key.", HttpStatusCode.BadRequest);
            }

            operation = IsDirectoryBucketControl(request) && request.PathKind == S3PathKind.Service && request.Method == "GET"
                ? "ListDirectoryBuckets"
                : request.Method == "OPTIONS" && request.PathKind != S3PathKind.Service ? "PreflightRequest"
                : InternalS3Client.IsPostObject(request) ? "PostObject" : Route(request);
            if (operation is null)
            {
                throw S3Errors.MethodNotAllowed(request.Method, request.PathKind switch
                {
                    S3PathKind.Service => "SERVICE",
                    S3PathKind.Bucket => "BUCKET",
                    _ => "OBJECT",
                });
            }

            request.Operation = operation;
#if NET
            activity = WireTracing.Start("S3", operation);
#endif
            var client = new InternalS3Client(cloud, request.Region) { Request = request, RequestId = requestId };
            if (operation == "PreflightRequest")
            {
                client.AnswerPreflight(response);
            }
            else
            {
                if (operation == "PostObject")
                {
                    client.PostObject(request, response);
                }
                else
                {
                    await DispatchAsync(operation, request, client, response, cancellationToken).ConfigureAwait(false);
                }

                InternalS3Client.AddCorsHeaders(request, cloud, response);
            }

            if (client.ResponseStatus is { } status)
            {
                response.Status = status;
            }

            // A part or byte range of an object is Partial Content.
            if (operation is "GetObject" or "HeadObject" && response.Header("Content-Range") is not null)
            {
                response.Status = 206;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (S3Exception ex) when (ex.ErrorCode == BareRefusal)
        {
            failure = ex;
            response = new S3WireResponse { Status = (int)ex.StatusCode };
        }
        catch (S3Exception ex)
        {
            failure = ex;
            response = Error(ex.ErrorCode ?? "InternalError", ex.Message, ex.StatusCode, ex.Details, ex.Headers, requestId);
            InternalS3Client.AddDirectoryBucketRegion(request, cloud, response);
        }
        catch (AwsServiceException ex)
        {
            failure = ex;
            response = Error(ex.ErrorCode ?? "InternalError", ex.Message, ex.StatusCode, [], [], requestId);
        }
#pragma warning disable CA1031 // Any other failure is an internal error, answered in the protocol.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ex;
            response = Error("InternalError", "We encountered an internal error. Please try again.", HttpStatusCode.InternalServerError, [], [], requestId);
        }

#if NET
        WireTracing.Complete(activity, (HttpStatusCode)response.Status, failure);
        activity?.Dispose();
#else
        _ = failure;
#endif
        response.SetHeader("x-amz-request-id", requestId);
        response.SetHeader("x-amz-id-2", NewHostId());
        response.SetHeader("Date", S3Wire.FormatTimestamp(cloud.TimeProvider.GetUtcNow().UtcDateTime, "rfc822"));
        response.SetHeader("Server", "AmazonS3");
        if (request.Method == "HEAD" || response.Status is 204 or 304)
        {
            response.OmitBody = true;
        }

        return response;
    }

    /// <summary>The code of a refusal S3 answers with its status alone: no error document.</summary>
    private const string BareRefusal = "<bare>";

    /// <summary>Whether the request was sent to S3 Express's control endpoint (<c>s3express-control.{region}.amazonaws.com</c>).</summary>
    private static bool IsDirectoryBucketControl(S3WireRequest request) =>
        request.Host.StartsWith("s3express-control.", StringComparison.OrdinalIgnoreCase);

    /// <summary>S3's error document, over whatever the operation wrote so far.</summary>
    private static S3WireResponse Error(
        string code,
        string message,
        HttpStatusCode status,
        IReadOnlyList<KeyValuePair<string, string>> details,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        string requestId)
    {
        var response = new S3WireResponse { Status = (int)status };
        foreach (var header in headers)
        {
            response.SetHeader(header.Key, header.Value);
        }

        if (status == HttpStatusCode.NotModified)
        {
            return response;
        }

        var writer = response.StartXml();
        writer.RootNamespace = false;
        writer.StartElement("Error");
        writer.Element("Code", code);
        writer.Element("Message", message);
        foreach (var detail in details)
        {
            writer.Element(detail.Key, detail.Value);
        }

        writer.Element("RequestId", requestId);
        writer.Element("HostId", NewHostId());
        writer.EndElement();
        return response;
    }

    /// <summary>A request id as S3 writes one: 16 upper-case hexadecimal digits.</summary>
    private static string NewRequestId()
    {
        var bytes = new byte[8];
        Random.GetBytes(bytes);
        var text = new StringBuilder(16);
        foreach (var b in bytes)
        {
            text.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>An extended request id (<c>x-amz-id-2</c>, <c>HostId</c>): 48 random bytes in base64.</summary>
    private static string NewHostId()
    {
        var bytes = new byte[48];
        Random.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }
}

/// <summary>
/// The <c>aws-chunked</c> content encoding SDKs stream S3 uploads with: the body is a series of
/// <c>size;chunk-signature=...\r\ndata\r\n</c> chunks ending with a zero-size chunk, followed by
/// trailing headers (the checksum the SDK computed while sending). Decoding replaces the body with
/// the data, moves the trailers into the headers, and removes <c>aws-chunked</c> from
/// <c>Content-Encoding</c>, so the operations see the request as if it had been sent whole.
/// </summary>
internal static class S3ChunkedBody
{
    public static void Decode(S3WireRequest request)
    {
        var sha = request.Header("x-amz-content-sha256");
        var encoding = request.Header("Content-Encoding");
        var chunked = (sha is not null && sha.StartsWith("STREAMING-", StringComparison.Ordinal))
            || (encoding is not null && encoding.Split(',').Any(e => string.Equals(e.Trim(), "aws-chunked", StringComparison.OrdinalIgnoreCase)));
        if (!chunked || request.Body.Length == 0)
        {
            return;
        }

        var body = request.Body.Span;
        var data = new MemoryStream(body.Length);
        var position = 0;
        while (true)
        {
            var lineEnd = IndexOfCrLf(body, position);
            if (lineEnd < 0)
            {
                throw Incomplete();
            }

            var header = Encoding.ASCII.GetString(body.Slice(position, lineEnd - position).ToArray());
            var semicolon = header.IndexOf(';', StringComparison.Ordinal);
            var sizeText = (semicolon < 0 ? header : header.Substring(0, semicolon)).Trim();
            if (!int.TryParse(sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size) || size < 0)
            {
                throw Incomplete();
            }

            position = lineEnd + 2;
            if (size == 0)
            {
                break;
            }

            if (position + size > body.Length)
            {
                throw Incomplete();
            }

            data.Write(body.Slice(position, size).ToArray(), 0, size);
            position += size;
            if (position + 2 <= body.Length && body[position] == '\r' && body[position + 1] == '\n')
            {
                position += 2;
            }
        }

        // Trailing headers, one per line, up to an empty line or the end.
        while (position < body.Length)
        {
            var lineEnd = IndexOfCrLf(body, position);
            var line = Encoding.ASCII.GetString(body.Slice(position, (lineEnd < 0 ? body.Length : lineEnd) - position).ToArray());
            position = lineEnd < 0 ? body.Length : lineEnd + 2;
            if (line.Length == 0)
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                var name = line.Substring(0, colon).Trim();
                if (!string.Equals(name, "x-amz-trailer-signature", StringComparison.OrdinalIgnoreCase))
                {
                    request.SetHeader(name, line.Substring(colon + 1).Trim());
                }
            }
        }

        var decoded = data.ToArray();
        request.ReplaceBody(decoded);
        request.SetHeader("Content-Length", decoded.Length.ToString(CultureInfo.InvariantCulture));
        if (encoding is not null)
        {
            var remaining = encoding.Split(',')
                .Select(e => e.Trim())
                .Where(e => e.Length > 0 && !string.Equals(e, "aws-chunked", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (remaining.Count == 0)
            {
                request.RemoveHeader("Content-Encoding");
            }
            else
            {
                request.SetHeader("Content-Encoding", string.Join(",", remaining));
            }
        }
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

    private static S3Exception Incomplete() =>
        new("IncompleteBody", "The request body terminated unexpectedly", HttpStatusCode.BadRequest);
}
