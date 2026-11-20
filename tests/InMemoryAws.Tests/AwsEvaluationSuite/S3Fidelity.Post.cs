using System.Text;

namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>What the S3 POST Object (browser form upload) ports share.</summary>
public static partial class S3Fidelity
{
    /// <summary>
    /// <c>_post(ctx, url, fields, filename, file_bytes, file_content_type)</c>: POSTs a
    /// <c>multipart/form-data</c> body (<c>key</c> first, the other fields in order, <c>file</c> last, as
    /// S3 requires) and returns the status and body; a 4xx or 5xx is an answer. One 307 is followed, as
    /// the checks follow it (none is answered here).
    /// </summary>
    public static async Task<(int Status, byte[] Body)> PostForm(
        FidelityContext ctx,
        string url,
        IReadOnlyList<KeyValuePair<string, string>> fields,
        string fileName,
        byte[] fileBytes,
        string? fileContentType = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(fileBytes);
        var boundary = $"----sim2real{Guid.NewGuid():N}";
        using var body = new MemoryStream();
        var ordered = fields.Where(f => f.Key == "key").Concat(fields.Where(f => f.Key != "key"));
        foreach (var (name, value) in ordered)
        {
            body.Write(Encoding.UTF8.GetBytes($"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n"));
        }

        var contentTypeLine = fileContentType is null ? string.Empty : $"\r\nContent-Type: {fileContentType}";
        body.Write(Encoding.UTF8.GetBytes($"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{fileName}\"{contentTypeLine}\r\n\r\n"));
        body.Write(fileBytes);
        body.Write(Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n"));
        var bytes = body.ToArray();
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = $"multipart/form-data; boundary={boundary}",
            ["Content-Length"] = bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var (status, answered, answer) = await Send(ctx, "POST", url, headers, bytes);
        if (status == 307 && answered.TryGetValue("location", out var location))
        {
            (status, _, answer) = await Send(ctx, "POST", location, headers, bytes);
        }

        return (status, answer);
    }
}
