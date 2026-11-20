#pragma warning disable CA1308 // S3 keeps metadata names in lower case.
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using InMemoryAws.Http;

namespace InMemoryAws.S3;

/// <summary>
/// POST Object: a browser form upload (<c>multipart/form-data</c>) to a bucket, admitted by the policy
/// document the form carries.
/// </summary>
/// <remarks>
/// The policy's expiration is read against the cloud's clock, and each of its conditions (exact
/// matches, <c>eq</c>, <c>starts-with</c>, <c>content-length-range</c>) must hold; a form field no
/// condition covers is refused. The policy's signature is checked only when the cloud knows the
/// secret of the form's access key (<see cref="InMemoryAwsCloud.SecretAccessKeys"/>).
/// </remarks>
internal sealed partial class InternalS3Client
{
    /// <summary>The form fields a policy need not mention.</summary>
    private static readonly HashSet<string> UnconditionedFormFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "file",
        "policy",
        "x-amz-signature",
        "signature",
        "awsaccesskeyid",
        "bucket",
    };

    /// <summary>Whether <paramref name="request"/> is a POST Object form upload: a <c>multipart/form-data</c> POST to a bucket.</summary>
    internal static bool IsPostObject(S3WireRequest request) =>
        request.Method == "POST"
        && request.PathKind == S3PathKind.Bucket
        && request.Header("Content-Type") is { } contentType
        && contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Stores the form's file under its <c>key</c> field (with <c>${filename}</c> replaced by the file's
    /// name), once the policy admits the form; answers as <c>success_action_status</c> asks (204 by
    /// default, or 201 with a <c>PostResponse</c> document).
    /// </summary>
    internal void PostObject(S3WireRequest request, S3WireResponse response)
    {
        var form = ParseForm(request);
        if (form.File is not { } data)
        {
            throw S3Errors.InvalidArgument("POST requires exactly one file upload per request.", "file", "0");
        }

        if (form.Field("key") is not { } keyField)
        {
            throw S3Errors.InvalidArgument("Bucket POST must contain a field named 'key'.  If it is specified, please check the order of the fields.", "key", string.Empty);
        }

        #pragma warning disable CA1307 // string.Replace is ordinal; netstandard2.0 has no overload taking a comparison.
        var key = CheckKey(keyField.Replace("${filename}", form.FileName ?? string.Empty));
#pragma warning restore CA1307
        if (form.Field("policy") is { } policy)
        {
            CheckPostPolicy(request, form, policy, data.Length);
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in form.Fields)
        {
            if (field.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
            {
                metadata[field.Key.Substring("x-amz-meta-".Length).ToLowerInvariant()] = field.Value;
            }
        }

        CheckMetadataSize(metadata);
        var storageClass = form.Field("x-amz-storage-class") is { } requestedClass ? CheckStorageClass(requestedClass) : "STANDARD";
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var version = new S3ObjectVersion
            {
                Key = key,
                VersionId = NextVersionId(bucket),
                Data = data,
                ETag = S3Checksums.ETag(data),
                LastModified = NowToTheSecond,
                ContentType = form.Field("Content-Type") ?? "binary/octet-stream",
                ContentEncoding = form.Field("Content-Encoding"),
                ContentDisposition = form.Field("Content-Disposition"),
                CacheControl = form.Field("Cache-Control"),
                Expires = form.Field("Expires"),
                WebsiteRedirectLocation = form.Field("x-amz-website-redirect-location"),
                Metadata = metadata,
                StorageClass = storageClass,
                ServerSideEncryption = form.Field("x-amz-server-side-encryption") ?? "AES256",
                ChecksumAlgorithm = "CRC64NVME",
                Checksum = S3Checksums.Compute("CRC64NVME", data),
                ChecksumType = "FULL_OBJECT",
                OwnerId = OwnerId,
            };
            bucket.Add(version);
            OnObjectWritten(bucket, version, "ObjectCreated:Post");
            ReplicateWrite(bucket, version);

            var location = $"https://{bucket.Name}.s3.{bucket.Region}.amazonaws.com/{EncodeKeyForUrl(key)}";
            response.SetHeader("ETag", version.ETag);
            response.SetHeader("Location", location);
            if (VersionIdHeader(bucket, version) is { } versionId)
            {
                response.SetHeader("x-amz-version-id", versionId);
            }

            switch (form.Field("success_action_status"))
            {
                case "200":
                    response.Status = 200;
                    break;
                case "201":
                    response.Status = 201;
                    var writer = response.StartXml();
                    writer.RootNamespace = false;
                    writer.StartElement("PostResponse");
                    writer.Element("Location", location);
                    writer.Element("Bucket", bucket.Name);
                    writer.Element("Key", key);
                    writer.Element("ETag", version.ETag);
                    writer.EndElement();
                    break;
                default:
                    response.Status = 204;
                    break;
            }
        }
    }

    /// <summary>
    /// Checks the form against its policy: the policy's signature (when the access key is known), its
    /// expiration, every condition, and that no field goes unmentioned.
    /// </summary>
    private void CheckPostPolicy(S3WireRequest request, PostForm form, string policy, int fileSize)
    {
        if (S3RequestAuthentication.KnownCredential(_cloud, form.Field("x-amz-credential")) is { } known
            && form.Field("x-amz-signature") is { } signature)
        {
            var key = S3RequestAuthentication.SigningKey(known.Secret, known.Date, known.Region, known.Service);
            if (!string.Equals(S3Checksums.Hex(S3RequestAuthentication.Hmac(key, policy)), signature, StringComparison.OrdinalIgnoreCase))
            {
                throw new S3Exception(
                    "SignatureDoesNotMatch",
                    "The request signature we calculated does not match the signature you provided. Check your key and signing method.",
                    HttpStatusCode.Forbidden,
                    ("AWSAccessKeyId", known.AccessKey),
                    ("StringToSign", policy),
                    ("SignatureProvided", signature));
            }
        }

        JsonElement document;
        try
        {
            using var parsed = JsonDocument.Parse(Convert.FromBase64String(policy));
            document = parsed.RootElement.Clone();
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw S3Errors.InvalidArgument("Invalid Policy: Invalid JSON.", "Policy", policy);
        }

        if (document.ValueKind != JsonValueKind.Object
            || !document.TryGetProperty("expiration", out var expirationElement)
            || expirationElement.ValueKind != JsonValueKind.String
            || !DateTime.TryParse(expirationElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var expiration))
        {
            throw S3Errors.InvalidArgument("Invalid Policy: Invalid 'expiration' value.", "Policy", policy);
        }

        if (Now > expiration)
        {
            throw S3Errors.AccessDenied("Invalid according to Policy: Policy expired.");
        }

        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (document.TryGetProperty("conditions", out var conditions) && conditions.ValueKind == JsonValueKind.Array)
        {
            foreach (var condition in conditions.EnumerateArray())
            {
                CheckPostCondition(request, form, condition, fileSize, covered);
            }
        }

        foreach (var field in form.Fields)
        {
            if (!UnconditionedFormFields.Contains(field.Key)
                && !field.Key.StartsWith("x-ignore-", StringComparison.OrdinalIgnoreCase)
                && !covered.Contains(field.Key))
            {
                throw S3Errors.AccessDenied($"Invalid according to Policy: Extra input fields: {field.Key}");
            }
        }
    }

    /// <summary>Checks one policy condition, noting the field it covers.</summary>
    private static void CheckPostCondition(S3WireRequest request, PostForm form, JsonElement condition, int fileSize, HashSet<string> covered)
    {
        if (condition.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in condition.EnumerateObject())
            {
                covered.Add(property.Name);
                var expected = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText();
                if (!string.Equals(FormValue(request, form, property.Name), expected, StringComparison.Ordinal))
                {
                    throw PolicyConditionFailed("eq", "$" + property.Name, expected);
                }
            }

            return;
        }

        if (condition.ValueKind != JsonValueKind.Array || condition.GetArrayLength() != 3)
        {
            throw S3Errors.InvalidArgument("Invalid Policy: Invalid Condition.", "Policy", condition.GetRawText());
        }

        var operation = condition[0].GetString();
        if (string.Equals(operation, "content-length-range", StringComparison.OrdinalIgnoreCase))
        {
            var min = condition[1].ValueKind == JsonValueKind.Number ? condition[1].GetInt64() : long.Parse(condition[1].GetString()!, CultureInfo.InvariantCulture);
            var max = condition[2].ValueKind == JsonValueKind.Number ? condition[2].GetInt64() : long.Parse(condition[2].GetString()!, CultureInfo.InvariantCulture);
            if (fileSize > max)
            {
                throw S3Errors.EntityTooLarge(fileSize, max);
            }

            if (fileSize < min)
            {
                throw new S3Exception(
                    "EntityTooSmall",
                    "Your proposed upload is smaller than the minimum allowed size",
                    HttpStatusCode.BadRequest,
                    ("ProposedSize", fileSize.ToString(CultureInfo.InvariantCulture)),
                    ("MinSizeAllowed", min.ToString(CultureInfo.InvariantCulture)));
            }

            return;
        }

        var reference = condition[1].GetString() ?? string.Empty;
        var value = condition[2].GetString() ?? string.Empty;
        var name = reference.Length > 0 && reference[0] == '$' ? reference.Substring(1) : reference;
        covered.Add(name);
        var actual = FormValue(request, form, name) ?? string.Empty;
        var holds = operation?.ToLowerInvariant() switch
        {
            "eq" => string.Equals(actual, value, StringComparison.Ordinal),
            "starts-with" => actual.StartsWith(value, StringComparison.Ordinal),
            _ => throw S3Errors.InvalidArgument("Invalid Policy: Invalid Condition.", "Policy", condition.GetRawText()),
        };
        if (!holds)
        {
            throw PolicyConditionFailed(operation!, reference, value);
        }
    }

    private static string? FormValue(S3WireRequest request, PostForm form, string name) =>
        string.Equals(name, "bucket", StringComparison.OrdinalIgnoreCase) ? form.Field("bucket") ?? request.Bucket : form.Field(name);

    private static S3Exception PolicyConditionFailed(string operation, string reference, string? value) =>
        S3Errors.AccessDenied($"Invalid according to Policy: Policy Condition failed: [\"{operation}\", \"{reference}\", \"{value}\"]");

    private static string EncodeKeyForUrl(string key) =>
        string.Join("/", key.Split('/').Select(Uri.EscapeDataString));

    /// <summary>A parsed form: its fields, by case-insensitive name, and its file part.</summary>
    private sealed class PostForm
    {
        public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);

        public byte[]? File { get; set; }

        public string? FileName { get; set; }

        public string? Field(string name) => Fields.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>
    /// Parses a <c>multipart/form-data</c> body. Fields after the <c>file</c> part are ignored, as S3
    /// ignores them.
    /// </summary>
    private static PostForm ParseForm(S3WireRequest request)
    {
        var contentType = request.Header("Content-Type") ?? string.Empty;
        var boundaryAt = contentType.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
        if (boundaryAt < 0)
        {
            throw MalformedPost();
        }

        var boundary = contentType.Substring(boundaryAt + "boundary=".Length).Split(';')[0].Trim().Trim('"');
        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var body = request.Body.ToArray();
        var form = new PostForm();
        var position = IndexOf(body, delimiter, 0);
        if (position < 0)
        {
            throw MalformedPost();
        }

        while (true)
        {
            position += delimiter.Length;
            if (position + 1 < body.Length && body[position] == '-' && body[position + 1] == '-')
            {
                break;
            }

            position = SkipCrLf(body, position);
            var headersEnd = IndexOf(body, "\r\n\r\n"u8.ToArray(), position);
            if (headersEnd < 0)
            {
                throw MalformedPost();
            }

            var partHeaders = Encoding.UTF8.GetString(body, position, headersEnd - position);
            var contentStart = headersEnd + 4;
            var next = IndexOf(body, delimiter, contentStart);
            if (next < 0)
            {
                throw MalformedPost();
            }

            var contentEnd = next >= 2 && body[next - 2] == '\r' && body[next - 1] == '\n' ? next - 2 : next;
            var (name, fileName) = Disposition(partHeaders);
            if (name is not null)
            {
                if (string.Equals(name, "file", StringComparison.OrdinalIgnoreCase))
                {
                    form.File = body.AsSpan(contentStart, contentEnd - contentStart).ToArray();
                    form.FileName = fileName;
                    break;
                }

                form.Fields[name] = Encoding.UTF8.GetString(body, contentStart, contentEnd - contentStart);
            }

            position = next;
        }

        return form;
    }

    /// <summary>The <c>name</c> and <c>filename</c> of a part's <c>Content-Disposition</c>.</summary>
    private static (string? Name, string? FileName) Disposition(string headers)
    {
        foreach (var line in headers.Split(["\r\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("Content-Disposition:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? name = null, fileName = null;
            foreach (var parameter in line.Substring("Content-Disposition:".Length).Split(';'))
            {
                var pair = parameter.Trim();
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0)
                {
                    continue;
                }

                var value = pair.Substring(eq + 1).Trim().Trim('"');
                switch (pair.Substring(0, eq).Trim().ToLowerInvariant())
                {
                    case "name":
                        name = value;
                        break;
                    case "filename":
                        fileName = value;
                        break;
                }
            }

            return (name, fileName);
        }

        return (null, null);
    }

    private static int SkipCrLf(byte[] body, int position) =>
        position + 1 < body.Length && body[position] == '\r' && body[position + 1] == '\n' ? position + 2 : position;

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (var i = start; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }

    private static S3Exception MalformedPost() =>
        new("MalformedPOSTRequest", "The body of your POST request is not well-formed multipart/form-data.", HttpStatusCode.BadRequest);
}
