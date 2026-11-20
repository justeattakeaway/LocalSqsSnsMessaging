#pragma warning disable CA1822 // The partial methods are declared on the instance.
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>
/// S3 Express One Zone directory buckets: creating and listing them, the sessions their data plane is
/// signed with (CreateSession), appends (<c>WriteOffsetBytes</c>), and the rules a directory bucket
/// imposes on listings and copies.
/// </summary>
internal sealed partial class InternalS3Client
{
    /// <summary>How long a session's credentials last: five minutes, as S3 Express mints them.</summary>
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(5);

    /// <summary>The Availability Zone ids of each region that offer S3 Express One Zone.</summary>
    private static readonly Dictionary<string, string[]> ExpressZones = new(StringComparer.Ordinal)
    {
        ["us-east-1"] = ["use1-az4", "use1-az5", "use1-az6"],
        ["us-east-2"] = ["use2-az1", "use2-az2"],
        ["us-west-2"] = ["usw2-az1", "usw2-az3", "usw2-az4"],
        ["ap-south-1"] = ["aps1-az1", "aps1-az3"],
        ["ap-northeast-1"] = ["apne1-az1", "apne1-az4"],
        ["eu-central-1"] = ["euc1-az1", "euc1-az2"],
        ["eu-west-1"] = ["euw1-az1", "euw1-az3"],
        ["eu-north-1"] = ["eun1-az1", "eun1-az2", "eun1-az3"],
    };

    /// <summary>A directory bucket's name: a base name, the zone id, and <c>--x-s3</c>.</summary>
    private static readonly Regex DirectoryBucketName = new(
        "^(?<base>[a-z0-9][a-z0-9.-]*[a-z0-9])--(?<zone>[a-z0-9]+-az[0-9]+)--x-s3$",
        RegexOptions.CultureInvariant);

    /// <summary>The operations a <c>ReadOnly</c> session may make.</summary>
    private static readonly HashSet<string> SessionReadOperations = new(StringComparer.Ordinal)
    {
        "GetObject", "HeadObject", "HeadBucket", "ListObjectsV2", "ListObjects", "ListMultipartUploads", "ListParts",
        "GetObjectAttributes", "CreateSession",
    };

    /// <summary>
    /// Creates a directory bucket: <c>{base}--{zone id}--x-s3</c>, in a zone of the request's region that
    /// offers S3 Express One Zone (another name is <c>InvalidBucketName</c>), configured as a
    /// single-zone directory bucket in that zone.
    /// </summary>
    private Task<CreateBucketOutput> CreateDirectoryBucket(string name, CreateBucketRequest request)
    {
        var configuration = request.CreateBucketConfiguration!;
        var match = DirectoryBucketName.Match(name);
        if (!match.Success || name.Length > 63 || !S3BucketNames.IsValid(match.Groups["base"].Value.PadRight(3, '0'))
            || !ExpressZones.TryGetValue(Region, out var zones) || !zones.Contains(match.Groups["zone"].Value))
        {
            throw S3Errors.InvalidBucketName(name);
        }

        if (configuration.Bucket!.Type is not (null or "Directory")
            || configuration.Bucket.DataRedundancy is not (null or "SingleAvailabilityZone")
            || configuration.Location?.Type is not (null or "AvailabilityZone"))
        {
            throw S3Errors.MalformedXml();
        }

        if (configuration.Location?.Name is { } zone && zone != match.Groups["zone"].Value)
        {
            throw S3Errors.InvalidBucketName(name);
        }

        lock (Store.Gate)
        {
            if (Store.Buckets.TryGetValue(name, out var existing))
            {
                throw existing.OwnerId == OwnerId ? S3Errors.BucketAlreadyOwnedByYou(name) : S3Errors.BucketAlreadyExists(name);
            }

            var bucket = new S3Bucket(name, Region, OwnerId, NowToTheSecond);
            Store.Buckets.Add(name, bucket);
            return Task.FromResult(new CreateBucketOutput { Location = Region, BucketArn = bucket.DirectoryArn(_cloud.CurrentAccountId) });
        }
    }

    /// <summary>The account's directory buckets, in name order, paged by <c>MaxDirectoryBuckets</c>.</summary>
    public override Task<ListDirectoryBucketsOutput> ListDirectoryBucketsAsync(ListDirectoryBucketsRequest request, CancellationToken cancellationToken)
    {
        if (request.MaxDirectoryBuckets is < 0 or > 1000)
        {
            throw S3Errors.InvalidArgument("Argument max-directory-buckets must be an integer between 0 and 1000", "max-directory-buckets",
                request.MaxDirectoryBuckets.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        lock (Store.Gate)
        {
            var after = DecodeToken(request.ContinuationToken);
            var buckets = Store.Buckets.Values
                .Where(b => b.OwnerId == OwnerId && b.IsDirectory && b.Region == Region)
                .OrderBy(b => b.Name, StringComparer.Ordinal)
                .Where(b => after is null || string.CompareOrdinal(b.Name, after) > 0)
                .ToList();
            var page = buckets.Take(request.MaxDirectoryBuckets is { } max and > 0 ? max : 1000).ToList();
            return Task.FromResult(new ListDirectoryBucketsOutput
            {
                Buckets = [.. page.Select(b => new Bucket
                {
                    Name = b.Name,
                    CreationDate = b.Created,
                    BucketRegion = b.Region,
                    BucketArn = b.DirectoryArn(_cloud.CurrentAccountId),
                })],
                ContinuationToken = page.Count < buckets.Count ? EncodeToken(page[^1].Name) : null,
            });
        }
    }

    /// <summary>
    /// Mints a session for a bucket: credentials that last five minutes, scoped to the bucket and to
    /// its mode (<c>ReadWrite</c>, or <c>ReadOnly</c>, which may only read). Every call mints a new one.
    /// </summary>
    public override Task<CreateSessionOutput> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken)
    {
        var mode = request.SessionMode ?? "ReadWrite";
        if (mode is not ("ReadWrite" or "ReadOnly"))
        {
            throw S3Errors.InvalidArgument("Invalid session mode", "x-amz-create-session-mode", mode);
        }

        lock (Store.Gate)
        {
            if (request.Bucket is { } name && name.EndsWith("--x-s3", StringComparison.Ordinal) && !Store.Buckets.ContainsKey(name))
            {
                throw new S3Exception("NoSuchBucket", "The specified bucket does not exist.", HttpStatusCode.NotFound, ("BucketName", name));
            }

            var bucket = GetBucket(request.Bucket);
            var now = Now;
            foreach (var expired in bucket.Sessions.Where(s => s.Value.Expiration <= now).Select(s => s.Key).ToList())
            {
                bucket.Sessions.Remove(expired);
            }

            var session = new S3ExpressSession(
                "ASIA" + RandomCharacters(16),
                RandomToken(30),
                RandomToken(96),
                mode,
                request.ServerSideEncryption ?? "AES256",
                new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc) + SessionLifetime);
            bucket.Sessions[session.Token] = session;
            return Task.FromResult(new CreateSessionOutput
            {
                Credentials = new SessionCredentials
                {
                    AccessKeyId = session.AccessKeyId,
                    SecretAccessKey = session.SecretAccessKey,
                    SessionToken = session.Token,
                    Expiration = session.Expiration,
                },
                ServerSideEncryption = session.ServerSideEncryption,
                SSEKMSKeyId = request.SSEKMSKeyId,
                BucketKeyEnabled = request.BucketKeyEnabled,
            });
        }
    }

    /// <summary>
    /// A request to a directory bucket for something it doesn't support is refused (see
    /// <see cref="CheckDirectoryOperation"/>). One signed with a session (<c>x-amz-s3session-token</c>) is refused
    /// (<c>AccessDenied</c>) when the session is unknown to the bucket (minted for another one), has
    /// expired, or is <c>ReadOnly</c> and the request writes. Requests without a session token (the
    /// control endpoint's, CreateSession's) are not checked.
    /// </summary>
    partial void AuthorizeSession(S3Bucket bucket)
    {
        if (!bucket.IsDirectory)
        {
            return;
        }

        CheckDirectoryOperation(Request?.Operation);
        if (Request?.Header("x-amz-s3session-token") is not { } token || Request.Operation == "CreateSession")
        {
            return;
        }

        if (!bucket.Sessions.TryGetValue(token, out var session) || session.Expiration <= Now
            || (session.Mode == "ReadOnly" && !SessionReadOperations.Contains(Request.Operation ?? string.Empty)))
        {
            throw S3Errors.AccessDenied();
        }
    }

    /// <summary>
    /// Refuses what a directory bucket doesn't support: object tags (<c>NotImplemented</c>), and the
    /// versioning, notification and replication configurations, whose documents S3 reads as a
    /// malformed <c>CreateBucketConfiguration</c> (<c>InvalidArgument</c>).
    /// </summary>
    private static void CheckDirectoryOperation(string? operation)
    {
        switch (operation)
        {
            case "PutObjectTagging" or "GetObjectTagging" or "DeleteObjectTagging":
                throw new S3Exception("NotImplemented", "This bucket does not support Object Tags.", HttpStatusCode.NotImplemented);
            case "PutBucketVersioning":
                throw S3Errors.InvalidArgument("VersioningConfiguration is not valid, expected CreateBucketConfiguration");
            case "PutBucketNotificationConfiguration":
                throw S3Errors.InvalidArgument("NotificationConfiguration is not valid, expected CreateBucketConfiguration");
            case "PutBucketReplication":
                throw S3Errors.InvalidArgument("ReplicationConfiguration is not valid, expected CreateBucketConfiguration");
        }
    }

    /// <summary>
    /// S3 names a directory bucket's region (<c>x-amz-bucket-region</c>) on every refusal of a request to
    /// it; without it, SDKs treat the refusal as a wrong-region answer and go looking for the bucket.
    /// </summary>
    internal static void AddDirectoryBucketRegion(Http.S3WireRequest request, InMemoryAwsCloud cloud, Http.S3WireResponse response)
    {
        if (request.Bucket is not { } name || !name.EndsWith("--x-s3", StringComparison.Ordinal) || response.Header("x-amz-bucket-region") is not null)
        {
            return;
        }

        lock (cloud.S3.Gate)
        {
            if (cloud.S3.Buckets.TryGetValue(name, out var bucket))
            {
                response.SetHeader("x-amz-bucket-region", bucket.Region);
            }
        }
    }

    /// <summary>The session the request is signed with, if it names one of the bucket's.</summary>
    private S3ExpressSession? RequestSession(S3Bucket bucket) =>
        Request?.Header("x-amz-s3session-token") is { } token && bucket.Sessions.TryGetValue(token, out var session) ? session : null;

    /// <summary>
    /// PutObject with <c>WriteOffsetBytes</c>: appends the body to an object in a directory bucket. The
    /// offset must be the object's current size (<c>InvalidWriteOffset</c> otherwise); an object that
    /// doesn't exist yet is created by an append at offset 0. The answer's <c>Size</c> is the object's
    /// size after the append.
    /// </summary>
    private Task<PutObjectOutput> AppendObject(PutObjectRequest request)
    {
        var body = request.Body?.ToArray() ?? [];
        VerifyContentMd5(request.ContentMD5, body);
        VerifyChecksums(request, request.ChecksumAlgorithm, body);
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            if (!bucket.IsDirectory)
            {
                throw S3Errors.NotImplemented("PutObject");
            }

            var key = CheckKey(request.Key);
            CheckSessionEncryption(bucket, request.ServerSideEncryption);
            var current = bucket.Current(key);
            var size = current is null || current.IsDeleteMarker ? 0 : current.Size;
            if (request.WriteOffsetBytes != size)
            {
                throw new S3Exception("InvalidWriteOffset", "The write offset value that you specified does not match the current object size.", HttpStatusCode.BadRequest);
            }

            var data = new byte[size + body.LongLength];
            if (current is not null && size > 0)
            {
                Buffer.BlockCopy(current.Data, 0, data, 0, current.Data.Length);
            }

            Buffer.BlockCopy(body, 0, data, (int)size, body.Length);
            var previous = size > 0 ? current : null;
            var version = new S3ObjectVersion
            {
                Key = key,
                VersionId = S3ObjectVersion.NullVersionId,
                Data = data,
                ETag = DirectoryMultipartETag(data),
                LastModified = NowToTheSecond,
                ContentType = previous?.ContentType ?? request.ContentType ?? "binary/octet-stream",
                ContentEncoding = previous?.ContentEncoding ?? request.ContentEncoding,
                ContentDisposition = previous?.ContentDisposition ?? request.ContentDisposition,
                ContentLanguage = previous?.ContentLanguage ?? request.ContentLanguage,
                CacheControl = previous?.CacheControl ?? request.CacheControl,
                Expires = previous?.Expires ?? request.Expires,
                Metadata = previous?.Metadata ?? request.Metadata ?? new Dictionary<string, string>(StringComparer.Ordinal),
                StorageClass = "EXPRESS_ONEZONE",
                ServerSideEncryption = previous?.ServerSideEncryption ?? "AES256",
                SseKmsKeyId = previous?.SseKmsKeyId,
                ChecksumAlgorithm = "CRC64NVME",
                Checksum = S3Checksums.Compute("CRC64NVME", data),
                ChecksumType = "FULL_OBJECT",
                Tags = previous?.Tags ?? [],
                OwnerId = OwnerId,
            };
            bucket.Add(version);
            OnObjectWritten(bucket, version, "ObjectCreated:Put");
            var output = new PutObjectOutput
            {
                ETag = version.ETag,
                ServerSideEncryption = version.ServerSideEncryption,
                SSEKMSKeyId = version.SseKmsKeyId,
                ChecksumType = version.ChecksumType,
                Size = version.Size,
            };
            SetChecksum(output, version.ChecksumAlgorithm, version.Checksum);
            return Task.FromResult(output);
        }
    }

    /// <summary>
    /// A write to a directory bucket is encrypted as its session was created: a request header asking
    /// for another server-side encryption is <c>InvalidRequest</c>.
    /// </summary>
    private void CheckSessionEncryption(S3Bucket bucket, string? serverSideEncryption)
    {
        if (serverSideEncryption is null || RequestSession(bucket) is not { } session || session.ServerSideEncryption == serverSideEncryption)
        {
            return;
        }

        throw S3Errors.InvalidRequest(
            $"Session was created with ServerSideEncryption as {EncryptionName(session.ServerSideEncryption)} but request header specifies {EncryptionName(serverSideEncryption)}.");

        static string EncryptionName(string algorithm) => algorithm switch
        {
            "aws:kms" => "SSE-KMS",
            "aws:kms:dsse" => "DSSE-KMS",
            _ => algorithm,
        };
    }

    /// <summary>
    /// The ETag of an object a directory bucket assembles (a completed multipart upload, an append):
    /// an opaque digest of the whole object, not the MD5 of the parts' MD5s.
    /// </summary>
    private static string DirectoryMultipartETag(byte[] data) =>
        "\"" + S3Checksums.Hex(SHA256.HashData(data).Take(16).ToArray()) + "\"";

    /// <summary>
    /// A directory bucket lists only with <c>/</c> as the delimiter, only under a prefix that ends in
    /// it, and never after a <c>start-after</c> key.
    /// </summary>
    partial void CheckDirectoryListing(S3Bucket bucket, ListObjectsV2Request request)
    {
        if (!bucket.IsDirectory)
        {
            return;
        }

        if (request.StartAfter is not null)
        {
            throw new S3Exception("NotSupported", "This bucket does not support start-after query parameter for ListObjectsV2 API.", HttpStatusCode.NotImplemented);
        }

        if (request.Delimiter is not null && request.Delimiter != "/")
        {
            throw S3Errors.InvalidRequest("This bucket only supports '/' as a delimiter");
        }

        if (!string.IsNullOrEmpty(request.Prefix) && request.Prefix![request.Prefix.Length - 1] != '/')
        {
            throw S3Errors.InvalidRequest("This bucket does not support a prefix that does not end in a delimiter. Specify a prefix path ending with a delimiter and try again.");
        }
    }

    /// <summary>
    /// A directory bucket lists the directory of a multipart upload in progress as a common prefix,
    /// as soon as the upload starts and until it is completed or aborted.
    /// </summary>
    partial void ListInflightUploads(S3Bucket bucket, ListObjectsV2Request request, ListObjectsV2Output output)
    {
        if (!bucket.IsDirectory || request.Delimiter != "/" || bucket.Uploads.Count == 0)
        {
            return;
        }

        var prefix = request.Prefix ?? string.Empty;
        var prefixes = new SortedSet<string>((output.CommonPrefixes ?? []).Select(p => p.Prefix ?? string.Empty), S3KeyComparer.Instance);
        var added = false;
        foreach (var upload in bucket.Uploads.Values)
        {
            if (!upload.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var slash = upload.Key.IndexOf('/', prefix.Length);
            if (slash >= 0 && prefixes.Add(upload.Key.Substring(0, slash + 1)))
            {
                added = true;
            }
        }

        if (added)
        {
            output.CommonPrefixes = [.. prefixes.Select(p => new CommonPrefix { Prefix = p })];
            output.KeyCount = (output.Contents?.Count ?? 0) + output.CommonPrefixes.Count;
        }
    }

    /// <summary>
    /// A copy into a directory bucket can't carry object tags over from its source, nor be encrypted
    /// with a customer key (<c>NotImplemented</c>).
    /// </summary>
    private static void CheckDirectoryCopy(S3Bucket bucket, S3ObjectVersion source, CopyObjectRequest request)
    {
        if (!bucket.IsDirectory)
        {
            return;
        }

        if (request.TaggingDirective != "REPLACE" && source.Tags.Count > 0)
        {
            throw new S3Exception("NotImplemented", "This bucket does not support copying source objects with Object Tags.", HttpStatusCode.NotImplemented);
        }

        if (request.SSECustomerAlgorithm is not null || request.SSECustomerKey is not null)
        {
            throw new S3Exception("NotImplemented", "This bucket does not support server side encryption with a customer provided key.", HttpStatusCode.NotImplemented);
        }
    }

    private static string RandomCharacters(int count)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new byte[count];
        Random.GetBytes(bytes);
        var chars = new char[count];
        for (var i = 0; i < count; i++)
        {
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        }

        return new string(chars);
    }
}

/// <summary>An S3 Express session CreateSession minted for a directory bucket.</summary>
internal sealed record S3ExpressSession(
    string AccessKeyId,
    string SecretAccessKey,
    string Token,
    string Mode,
    string ServerSideEncryption,
    DateTime Expiration);

/// <summary>What a bucket keeps for S3 Express One Zone.</summary>
internal sealed partial class S3Bucket
{
    /// <summary>Whether this is a directory bucket (S3 Express One Zone): its name ends <c>--x-s3</c>, which no general purpose bucket's may.</summary>
    public bool IsDirectory => Name.EndsWith("--x-s3", StringComparison.Ordinal);

    /// <summary>The sessions CreateSession minted for this bucket, by session token.</summary>
    public Dictionary<string, S3ExpressSession> Sessions { get; } = new(StringComparer.Ordinal);

    /// <summary>A directory bucket's ARN: <c>arn:aws:s3express:region:account:bucket/name</c>.</summary>
    public string DirectoryArn(string accountId) => $"arn:aws:s3express:{Region}:{accountId}:bucket/{Name}";
}
