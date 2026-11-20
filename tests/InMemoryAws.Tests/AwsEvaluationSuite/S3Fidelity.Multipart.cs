using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;

namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>
/// What the multipart, checksum and copy ports share: a tagged bucket (<c>_new_bucket</c> with
/// <c>put_bucket_tagging</c>), the checks' <c>_error</c> helpers, boto3's view of user metadata, and
/// the digests the checks compute client side (<c>hashlib</c>, <c>zlib.crc32</c>, CRC32C, CRC64NVME).
/// </summary>
public static partial class S3Fidelity
{
    /// <summary>
    /// <c>_new_bucket(ctx, s3)</c> with <c>put_bucket_tagging(TagSet=[{tagKey: tagValue}])</c>: a bucket
    /// created as <see cref="CreateBucket"/> creates one, then tagged.
    /// </summary>
    public static async Task<string> CreateTaggedBucket(FidelityContext ctx, string tagKey, string tagValue, string prefix = "sim2real-behav-bkt-")
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var name = await CreateBucket(ctx, prefix);
        await ctx.S3.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = name,
            TagSet = [new Tag { Key = tagKey, Value = tagValue }],
        });
        return name;
    }

    /// <summary>
    /// The checks' <c>_error(fn)</c>: runs <paramref name="call"/> and returns the error it raised; a
    /// call that succeeds fails the check with <paramref name="onSuccess"/>.
    /// </summary>
    public static async Task<AwsError> ExpectError(Func<Task> call, string onSuccess = "expected a ClientError, but the call SUCCEEDED")
    {
        return await FidelityContext.CaptureError(call)
            ?? throw FidelityAssert.Fail(onSuccess);
    }

    /// <summary><c>(err["Code"], err["Message"])</c> of a call that must fail.</summary>
    public static async Task<(string? Code, string? Message)> ErrorPair(Func<Task> call, string onSuccess = "expected a ClientError, but the call SUCCEEDED")
    {
        var error = await ExpectError(call, onSuccess);
        return (error.Code, error.Message);
    }

    /// <summary>
    /// <c>(err["Code"], err["Message"])</c> of a HEAD request that must fail. A HEAD error has no body,
    /// so botocore reports the status code as the code and the status's reason phrase as the message
    /// (<c>("404", "Not Found")</c>); the .NET SDK gives the same response its own names.
    /// </summary>
    public static async Task<(string? Code, string? Message)> HeadErrorPair(Func<Task> call, string onSuccess = "expected a ClientError, but the call SUCCEEDED")
    {
        var error = await ExpectError(call, onSuccess);
        return HeadError(error);
    }

    /// <summary>
    /// Sends a request the SDK can't make (a header value it rewrites) straight to the cloud, path
    /// style, and returns its status and, for an error, S3's <c>Code</c> and <c>Message</c>.
    /// </summary>
    public static async Task<(int Status, string? Code, string? Message)> SendRaw(
        FidelityContext ctx, HttpMethod method, string path, params (string Name, string Value)[] headers)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var http = ctx.RawHttpClient(InMemoryAws.Http.AwsServiceType.S3);
        using var request = new HttpRequestMessage(method, path);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await http.SendAsync(request, ctx.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(ctx.CancellationToken);
        if (response.IsSuccessStatusCode || body.Length == 0)
        {
            return ((int)response.StatusCode, null, null);
        }

        var error = System.Xml.Linq.XDocument.Parse(body).Root;
        return ((int)response.StatusCode, error?.Element("Code")?.Value, error?.Element("Message")?.Value);
    }

    /// <summary>
    /// <c>complete_multipart_upload(MultipartUpload={"Parts": parts})</c> with the parts in the order
    /// given. The .NET SDK sorts <c>PartETags</c> by part number before it sends them, so this sends
    /// the request's XML itself, path style, and raises an error response as the SDK would.
    /// </summary>
    public static async Task CompleteInListedOrder(FidelityContext ctx, string bucket, string key, string uploadId, List<PartETag> parts)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(parts);
        var xml = new System.Text.StringBuilder("<CompleteMultipartUpload xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">");
        foreach (var part in parts)
        {
            xml.Append("<Part><ETag>").Append(System.Security.SecurityElement.Escape(part.ETag))
                .Append("</ETag><PartNumber>").Append(part.PartNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append("</PartNumber></Part>");
        }

        xml.Append("</CompleteMultipartUpload>");
        var http = ctx.RawHttpClient(InMemoryAws.Http.AwsServiceType.S3);
        var path = $"{bucket}/{Uri.EscapeDataString(key)}?uploadId={Uri.EscapeDataString(uploadId)}";
        using var content = new StringContent(xml.ToString(), System.Text.Encoding.UTF8, "application/xml");
        using var response = await http.PostAsync(new Uri(path, UriKind.Relative), content, ctx.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(ctx.CancellationToken);
        var root = body.Length == 0 ? null : System.Xml.Linq.XDocument.Parse(body).Root;
        if (!response.IsSuccessStatusCode || root?.Name.LocalName == "Error")
        {
            throw new AmazonS3Exception(root?.Element("Message")?.Value)
            {
                ErrorCode = root?.Element("Code")?.Value,
                StatusCode = response.IsSuccessStatusCode ? System.Net.HttpStatusCode.OK : response.StatusCode,
            };
        }
    }

    /// <summary>
    /// <c>resp["Metadata"]</c> as boto3 gives it: the user metadata, names without <c>x-amz-meta-</c>.
    /// </summary>
    public static Dictionary<string, string> UserMetadata(MetadataCollection metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in metadata.Keys)
        {
            var bare = name.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase) ? name["x-amz-meta-".Length..] : name;
            result[bare] = metadata[name];
        }

        return result;
    }

    /// <summary><c>os.urandom(n)</c>.</summary>
    public static byte[] RandomBytes(int count) => RandomNumberGenerator.GetBytes(count);

    /// <summary><c>bytes([b]) * n</c>, or <c>b"x" * n</c>.</summary>
    public static byte[] Repeat(char b, int count)
    {
        var data = new byte[count];
        Array.Fill(data, (byte)b);
        return data;
    }

    /// <summary><c>hashlib.md5(data).hexdigest()</c>.</summary>
    public static string Md5Hex(byte[] data) => Convert.ToHexStringLower(Md5(data));

    /// <summary><c>hashlib.md5(data).digest()</c>.</summary>
    public static byte[] Md5(byte[] data)
    {
#pragma warning disable CA5351 // S3's ETags are MD5.
        return MD5.HashData(data);
#pragma warning restore CA5351
    }

    /// <summary><c>base64.b64encode(hashlib.sha256(data).digest()).decode()</c>.</summary>
    public static string Sha256B64(byte[] data) => Convert.ToBase64String(SHA256.HashData(data));

    /// <summary><c>base64.b64encode(hashlib.sha1(data).digest()).decode()</c>.</summary>
    public static string Sha1B64(byte[] data)
    {
#pragma warning disable CA5350 // S3 offers SHA-1 checksums.
        return Convert.ToBase64String(SHA1.HashData(data));
#pragma warning restore CA5350
    }

    /// <summary><c>base64.b64encode(zlib.crc32(data).to_bytes(4, "big")).decode()</c>.</summary>
    public static string Crc32B64(byte[] data) => Convert.ToBase64String(BigEndian(Crc32(data)));

    /// <summary>The base64 big-endian CRC32C (Castagnoli) of <paramref name="data"/>.</summary>
    public static string Crc32CB64(byte[] data) => Convert.ToBase64String(BigEndian(Crc32C(data)));

    /// <summary>The base64 big-endian CRC64NVME of <paramref name="data"/>.</summary>
    public static string Crc64NvmeB64(byte[] data) => Convert.ToBase64String(BigEndian(Crc64Nvme(data)));

    /// <summary><c>zlib.crc32(data)</c>.</summary>
    public static uint Crc32(byte[] data) => ReflectedCrc32(data, 0xEDB88320u);

    /// <summary>CRC32C (Castagnoli), as S3 computes it.</summary>
    public static uint Crc32C(byte[] data) => ReflectedCrc32(data, 0x82F63B78u);

    /// <summary>CRC64NVME (reflected polynomial 0x9A6C9329AC4BC9B5), as S3 computes it.</summary>
    public static ulong Crc64Nvme(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var crc = ulong.MaxValue;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x9A6C9329AC4BC9B5ul : crc >> 1;
            }
        }

        return ~crc;
    }

    private static uint ReflectedCrc32(byte[] data, uint polynomial)
    {
        ArgumentNullException.ThrowIfNull(data);
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? polynomial ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static byte[] BigEndian(uint value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] BigEndian(ulong value) =>
        [(byte)(value >> 56), (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32), (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    /// <summary><c>{"PartNumber": n, "ETag": etag}</c> for CompleteMultipartUpload.</summary>
    public static PartETag Part(int partNumber, string etag) => new() { PartNumber = partNumber, ETag = etag };

    /// <summary>A cleanup that aborts an upload, ignoring one already gone (<c>NoSuchUpload</c>).</summary>
    public static Func<Task> AbortQuietly(IAmazonS3 s3, string bucket, string key, string uploadId) => async () =>
    {
        ArgumentNullException.ThrowIfNull(s3);
        try
        {
            await s3.AbortMultipartUploadAsync(bucket, key, uploadId);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "NoSuchUpload" or "NoSuchBucket" or "NoSuchKey")
        {
        }
    };

    /// <summary>The AWS CLI's default <c>multipart_threshold</c> and <c>multipart_chunksize</c>: 8 MiB.</summary>
    public const int CliChunkSize = 8 * 1024 * 1024;

    /// <summary>
    /// <c>aws s3 cp &lt;file&gt; s3://bucket/key [--checksum-algorithm X]</c> as the CLI's transfer
    /// manager makes it: a file over 8 MiB goes up as a multipart upload in 8 MiB parts, with the
    /// checksum algorithm asked for, or CRC32 (the CLI's default), on the upload and on every part,
    /// and the parts' checksums listed when it completes. A smaller file is one PutObject.
    /// </summary>
    public static async Task CliUpload(IAmazonS3 s3, string bucket, string key, byte[] payload, ChecksumAlgorithm? algorithm = null)
    {
        ArgumentNullException.ThrowIfNull(s3);
        ArgumentNullException.ThrowIfNull(payload);
        algorithm ??= ChecksumAlgorithm.CRC32;
        if (payload.Length < CliChunkSize)
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(payload), ChecksumAlgorithm = algorithm });
            return;
        }

        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key, ChecksumAlgorithm = algorithm })).UploadId;
        var parts = new List<PartETag>();
        for (int offset = 0, number = 1; offset < payload.Length; offset += CliChunkSize, number++)
        {
            var chunk = payload.AsSpan(offset, Math.Min(CliChunkSize, payload.Length - offset)).ToArray();
            var uploaded = await s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = uploadId,
                PartNumber = number,
                InputStream = new MemoryStream(chunk),
                ChecksumAlgorithm = algorithm,
            });
            parts.Add(new PartETag
            {
                PartNumber = number,
                ETag = uploaded.ETag,
                ChecksumCRC32 = uploaded.ChecksumCRC32,
                ChecksumCRC32C = uploaded.ChecksumCRC32C,
                ChecksumCRC64NVME = uploaded.ChecksumCRC64NVME,
                ChecksumSHA1 = uploaded.ChecksumSHA1,
                ChecksumSHA256 = uploaded.ChecksumSHA256,
            });
        }

        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartETags = parts });
    }

    /// <summary>
    /// <c>aws s3 cp s3://bucket/key &lt;file&gt;</c> as the CLI's transfer manager makes it: HeadObject
    /// for the size, then ranged GetObjects of 8 MiB each. Returns the downloaded bytes.
    /// </summary>
    public static async Task<byte[]> CliDownload(IAmazonS3 s3, string bucket, string key)
    {
        ArgumentNullException.ThrowIfNull(s3);
        var size = (await s3.GetObjectMetadataAsync(bucket, key)).ContentLength;
        using var all = new MemoryStream();
        for (long offset = 0; offset < size; offset += CliChunkSize)
        {
            using var part = await s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ByteRange = new ByteRange(offset, Math.Min(offset + CliChunkSize, size) - 1),
            });
            await part.ResponseStream.CopyToAsync(all);
        }

        return all.ToArray();
    }
}
