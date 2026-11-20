using System.Globalization;
using System.Net;
using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

internal sealed partial class InternalS3Client
{
    /// <summary>
    /// Writes an object: checks <c>Content-MD5</c> and any <c>x-amz-checksum-*</c> against the body,
    /// applies <c>If-None-Match: *</c> and <c>If-Match</c>, and stores a new version (replacing the
    /// <c>null</c> version unless versioning is enabled). With no checksum sent, S3 computes CRC64NVME.
    /// </summary>
    public override Task<PutObjectOutput> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken)
    {
        if (request.WriteOffsetBytes is not null)
        {
            return AppendObject(request);
        }

        var data = request.Body?.ToArray() ?? [];
        VerifyContentMd5(request.ContentMD5, data);
        var (algorithm, checksum) = VerifyChecksums(request, request.ChecksumAlgorithm, data);
        algorithm ??= "CRC64NVME";
        checksum ??= S3Checksums.Compute(algorithm, data);
        var tags = ParseTagHeader(request.Tagging);
        var metadata = request.Metadata ?? new Dictionary<string, string>(StringComparer.Ordinal);
        CheckMetadataSize(metadata);
        var storageClass = CheckStorageClass(request.StorageClass);
        var sseCustomerKeyMd5 = SseCustomerKeyMd5(request.SSECustomerAlgorithm, request.SSECustomerKey, request.SSECustomerKeyMD5);

        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var key = CheckKey(request.Key);
            CheckWriteConditions(bucket, key, request.IfMatch, request.IfNoneMatch);
            var version = new S3ObjectVersion
            {
                Key = key,
                VersionId = NextVersionId(bucket),
                Data = data,
                ETag = S3Checksums.ETag(data),
                LastModified = NowToTheSecond,
                ContentType = request.ContentType ?? "binary/octet-stream",
                ContentEncoding = request.ContentEncoding,
                ContentDisposition = request.ContentDisposition,
                ContentLanguage = request.ContentLanguage,
                CacheControl = request.CacheControl,
                Expires = request.Expires,
                WebsiteRedirectLocation = request.WebsiteRedirectLocation,
                Metadata = metadata,
                StorageClass = storageClass,
                ServerSideEncryption = request.ServerSideEncryption ?? DefaultEncryption(bucket)?.SSEAlgorithm ?? "AES256",
                SseKmsKeyId = request.SSEKMSKeyId ?? (request.ServerSideEncryption is null ? DefaultEncryption(bucket)?.KMSMasterKeyID : null),
                BucketKeyEnabled = request.BucketKeyEnabled ?? (request.ServerSideEncryption is null ? DefaultBucketKey(bucket) : null),
                SseCustomerKeyMd5 = sseCustomerKeyMd5,
                ChecksumAlgorithm = algorithm,
                Checksum = checksum,
                ChecksumType = "FULL_OBJECT",
                Tags = tags,
                OwnerId = OwnerId,
            };
            ApplyObjectLock(bucket, version, request);
            bucket.Add(version);
            OnObjectWritten(bucket, version, "ObjectCreated:Put");
            ReplicateWrite(bucket, version);
            var output = new PutObjectOutput
            {
                ETag = version.ETag,

                // A write to a bucket whose versioning is suspended answers no version id, though the
                // version it wrote is the null one.
                VersionId = version.VersionId == S3ObjectVersion.NullVersionId ? null : VersionIdHeader(bucket, version),
                ServerSideEncryption = version.SseCustomerKeyMd5 is null ? version.ServerSideEncryption : null,
                SSEKMSKeyId = version.SseKmsKeyId,
                BucketKeyEnabled = version.BucketKeyEnabled,
                SSECustomerAlgorithm = version.SseCustomerKeyMd5 is null ? null : "AES256",
                SSECustomerKeyMD5 = version.SseCustomerKeyMd5,
                ChecksumType = version.ChecksumType,
            };
            SetChecksum(output, version.ChecksumAlgorithm, version.Checksum);
            return Task.FromResult(output);
        }
    }

    /// <summary>
    /// An object, or the part or range of it the request asks for. A key whose current version is a
    /// delete marker is <c>NoSuchKey</c> (with <c>x-amz-delete-marker</c>); a delete marker asked for
    /// by version id is <c>MethodNotAllowed</c>.
    /// </summary>
    public override Task<GetObjectOutput> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var version = ReadableVersion(bucket, request);
            CheckArchivedRead(version);
            var output = new GetObjectOutput();
            var (offset, length) = Describe(bucket, version, request, output);
            output.Body = new ReadOnlyMemory<byte>(version.Data, (int)offset, (int)length);
            return Task.FromResult(output);
        }
    }

    public override Task<HeadObjectOutput> HeadObjectAsync(HeadObjectRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var version = ReadableVersion(bucket, request);
            var output = new HeadObjectOutput();
            Describe(bucket, version, request, output);
            return Task.FromResult(output);
        }
    }

    /// <summary>
    /// Deletes an object. Without a version id: in a bucket that was never versioned the object goes;
    /// with versioning enabled a delete marker becomes its current version; with versioning suspended
    /// a delete marker replaces its <c>null</c> version. With a version id, that version goes for good.
    /// Deleting what isn't there succeeds.
    /// </summary>
    public override Task<DeleteObjectOutput> DeleteObjectAsync(DeleteObjectRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var key = CheckKey(request.Key);
            CheckDeleteIfMatch(bucket, key, request.IfMatch);
            var result = DeleteOne(bucket, key, request.VersionId, request.BypassGovernanceRetention == true);
            return Task.FromResult(new DeleteObjectOutput
            {
                DeleteMarker = result.DeleteMarker ? true : null,
                VersionId = result.VersionId,
            });
        }
    }

    /// <summary>What deleting one key or version did.</summary>
    private readonly record struct DeleteResult(bool DeleteMarker, string? VersionId);

    /// <summary>Deletes a key (or one version of it) as DeleteObject does; shared with DeleteObjects.</summary>
    private DeleteResult DeleteOne(S3Bucket bucket, string key, string? versionId, bool bypassGovernance)
    {
        if (versionId is not null)
        {
            CheckVersionIdFormat(versionId);
            var existing = bucket.Version(key, versionId);
            if (existing is null)
            {
                return new DeleteResult(false, versionId);
            }

            CheckDeletable(bucket, existing, bypassGovernance);
            bucket.Remove(key, versionId);
            OnObjectRemoved(bucket, existing, "ObjectRemoved:Delete");
            return new DeleteResult(existing.IsDeleteMarker, versionId);
        }

        if (bucket.Versioning is null)
        {
            var current = bucket.Current(key);
            if (current is not null)
            {
                bucket.Remove(key, current.VersionId);
                OnObjectRemoved(bucket, current, "ObjectRemoved:Delete");
            }

            return new DeleteResult(false, null);
        }

        var marker = new S3ObjectVersion
        {
            Key = key,
            VersionId = NextVersionId(bucket),
            IsDeleteMarker = true,
            LastModified = NowToTheSecond,
            OwnerId = OwnerId,
        };
        bucket.Add(marker);
        OnObjectRemoved(bucket, marker, "ObjectRemoved:DeleteMarkerCreated");
        ReplicateDeleteMarker(bucket, marker);
        return new DeleteResult(true, marker.VersionId);
    }

    /// <summary>Refuses to delete a version Object Lock protects; answers nothing for a bucket without Object Lock.</summary>
    partial void CheckDeletableCore(S3Bucket bucket, S3ObjectVersion version, bool bypassGovernance);

    private void CheckDeletable(S3Bucket bucket, S3ObjectVersion version, bool bypassGovernance) =>
        CheckDeletableCore(bucket, version, bypassGovernance);

    /// <summary>
    /// Deletes up to 1000 keys or versions, answering each in <c>Deleted</c> (unless <c>Quiet</c>) or
    /// <c>Errors</c>.
    /// </summary>
    public override Task<DeleteObjectsOutput> DeleteObjectsAsync(DeleteObjectsRequest request, CancellationToken cancellationToken)
    {
        var objects = request.Delete?.Objects;
        if (objects is null || objects.Count == 0 || objects.Count > 1000)
        {
            throw S3Errors.MalformedXml();
        }

        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var output = new DeleteObjectsOutput();
            foreach (var item in objects)
            {
                try
                {
                    var key = item.Key ?? throw S3Errors.MalformedXml();
                    CheckDeleteConditions(bucket, key, item);

                    // A version id S3 could never have minted is NoSuchVersion for its key here, where
                    // GetObject and DeleteObject refuse it as InvalidArgument.
                    if (item.VersionId is { } versionId && versionId != S3ObjectVersion.NullVersionId && !S3VersionIds.CouldBeMinted(versionId))
                    {
                        throw S3Errors.NoSuchVersion(key, versionId);
                    }

                    var result = DeleteOne(bucket, key, item.VersionId, request.BypassGovernanceRetention == true);
                    if (request.Delete!.Quiet != true)
                    {
                        (output.Deleted ??= []).Add(new DeletedObject
                        {
                            Key = key,
                            VersionId = item.VersionId,
                            DeleteMarker = result.DeleteMarker ? true : null,
                            DeleteMarkerVersionId = result.DeleteMarker ? result.VersionId : null,
                        });
                    }
                }
                catch (S3Exception ex) when (ex.ErrorCode != "MalformedXML")
                {
                    (output.Errors ??= []).Add(new S3Error { Key = item.Key, VersionId = item.VersionId, Code = ex.ErrorCode, Message = ex.Message });
                }
            }

            return Task.FromResult(output);
        }
    }

    /// <summary>
    /// Copies an object (or one version of it) to a key. Metadata and tags are copied unless the
    /// directives say <c>REPLACE</c>; copying an object onto itself must change something.
    /// </summary>
    public override Task<CopyObjectOutput> CopyObjectAsync(CopyObjectRequest request, CancellationToken cancellationToken)
    {
        var (sourceBucketName, sourceKey, sourceVersionId) = ParseCopySource(request.CopySource);
        if (request.MetadataDirective is not (null or "COPY" or "REPLACE"))
        {
            throw S3Errors.InvalidArgument("Unknown metadata directive.", "x-amz-metadata-directive", request.MetadataDirective);
        }

        if (request.TaggingDirective is not (null or "COPY" or "REPLACE"))
        {
            throw S3Errors.InvalidArgument("Unknown tagging directive.", "x-amz-tagging-directive", request.TaggingDirective);
        }

        var replaceMetadata = request.MetadataDirective == "REPLACE";
        var replaceTags = request.TaggingDirective == "REPLACE";
        var newAlgorithm = request.ChecksumAlgorithm is null ? null : S3Checksums.Normalize(request.ChecksumAlgorithm)
            ?? throw S3Errors.InvalidRequest("Checksum algorithm provided is unsupported. Please try again with any of the valid types: [CRC32, CRC32C, CRC64NVME, SHA1, SHA256]");
        var storageClass = request.StorageClass is null ? null : CheckStorageClass(request.StorageClass);

        lock (Store.Gate)
        {
            var sourceBucket = GetSourceBucket(sourceBucketName);
            var source = SourceVersion(sourceBucket, sourceKey, sourceVersionId);
            CheckCopySourceConditions(source, request.CopySourceIfMatch, request.CopySourceIfNoneMatch, request.CopySourceIfModifiedSince, request.CopySourceIfUnmodifiedSince);
            CheckArchivedCopySource(source);
            var bucket = GetBucket(request.Bucket);
            var key = CheckKey(request.Key);
            if (bucket == sourceBucket && key == sourceKey && sourceVersionId is null && !replaceMetadata && storageClass is null
                && request.WebsiteRedirectLocation is null && request.ServerSideEncryption is null && request.SSEKMSKeyId is null && newAlgorithm is null)
            {
                throw S3Errors.InvalidRequest("This copy request is illegal because it is trying to copy an object to itself without changing the object's metadata, storage class, website redirect location or encryption attributes.");
            }

            CheckWriteConditions(bucket, key, request.IfMatch, request.IfNoneMatch);
            var metadata = replaceMetadata ? request.Metadata ?? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(source.Metadata, StringComparer.Ordinal);
            CheckMetadataSize(metadata);
            var algorithm = newAlgorithm ?? (source.ChecksumType == "COMPOSITE" ? source.ChecksumAlgorithm ?? "CRC64NVME" : source.ChecksumAlgorithm) ?? "CRC64NVME";
            var version = new S3ObjectVersion
            {
                Key = key,
                VersionId = NextVersionId(bucket),
                Data = source.Data,
                ETag = S3Checksums.ETag(source.Data),
                LastModified = NowToTheSecond,
                ContentType = replaceMetadata ? request.ContentType ?? "binary/octet-stream" : source.ContentType,
                ContentEncoding = replaceMetadata ? request.ContentEncoding : source.ContentEncoding,
                ContentDisposition = replaceMetadata ? request.ContentDisposition : source.ContentDisposition,
                ContentLanguage = replaceMetadata ? request.ContentLanguage : source.ContentLanguage,
                CacheControl = replaceMetadata ? request.CacheControl : source.CacheControl,
                Expires = replaceMetadata ? request.Expires : source.Expires,
                WebsiteRedirectLocation = request.WebsiteRedirectLocation ?? (replaceMetadata ? null : source.WebsiteRedirectLocation),
                Metadata = metadata,
                StorageClass = storageClass ?? "STANDARD",
                ServerSideEncryption = request.ServerSideEncryption ?? DefaultEncryption(bucket)?.SSEAlgorithm ?? "AES256",
                SseKmsKeyId = request.SSEKMSKeyId ?? (request.ServerSideEncryption is null ? DefaultEncryption(bucket)?.KMSMasterKeyID : null),
                BucketKeyEnabled = request.BucketKeyEnabled ?? (request.ServerSideEncryption is null ? DefaultBucketKey(bucket) : null),
                ChecksumAlgorithm = algorithm,
                Checksum = S3Checksums.Compute(algorithm, source.Data),
                ChecksumType = "FULL_OBJECT",
                Tags = replaceTags ? ParseTagHeader(request.Tagging) : [.. source.Tags.Select(t => new Tag { Key = t.Key, Value = t.Value })],
                OwnerId = OwnerId,
            };
            OnCopying(bucket, source, version, request);
            ApplyObjectLock(bucket, version, request);
            bucket.Add(version);
            OnObjectWritten(bucket, version, "ObjectCreated:Copy");
            ReplicateWrite(bucket, version);
            var result = new CopyObjectResult { ETag = version.ETag, LastModified = version.LastModified, ChecksumType = version.ChecksumType };
            SetChecksum(result, version.ChecksumAlgorithm, version.Checksum);
            return Task.FromResult(new CopyObjectOutput
            {
                CopyObjectResult = result,
                VersionId = VersionIdHeader(bucket, version),
                CopySourceVersionId = sourceBucket.Versioning is null ? null : source.VersionId,
                ServerSideEncryption = version.ServerSideEncryption,
                SSEKMSKeyId = version.SseKmsKeyId,
                BucketKeyEnabled = version.BucketKeyEnabled,
            });
        }
    }

    /// <summary>
    /// Applies a write's Object Lock headers (or the bucket's default retention) to the new version
    /// before it is stored; refuses lock headers a bucket without Object Lock can't take.
    /// </summary>
    partial void ApplyObjectLock(S3Bucket bucket, S3ObjectVersion version, IS3ObjectLockMembers request);

    /// <summary>
    /// Called under the store's lock before a copy is stored: refuses what the destination can't take,
    /// and carries over what other areas keep on the source (annotations).
    /// </summary>
    partial void OnCopying(S3Bucket bucket, S3ObjectVersion source, S3ObjectVersion version, CopyObjectRequest request);

    /// <summary>Called under the store's lock after an object version is written (notifications hook in here).</summary>
    partial void OnObjectWritten(S3Bucket bucket, S3ObjectVersion version, string eventName);

    /// <summary>Called under the store's lock after an object version is removed or a delete marker written.</summary>
    partial void OnObjectRemoved(S3Bucket bucket, S3ObjectVersion version, string eventName);

    // ------------------------------------------------------------------------------------------
    // Reading
    // ------------------------------------------------------------------------------------------

    /// <summary>The version a GetObject or HeadObject reads, or the error S3 answers instead.</summary>
    private S3ObjectVersion ReadableVersion(S3Bucket bucket, IS3ObjectRead request)
    {
        var key = CheckKey(request.Key);
        S3ObjectVersion? version;
        if (request.VersionId is { } versionId)
        {
            CheckVersionIdFormat(versionId);
            version = bucket.Version(key, versionId);
            if (version is null)
            {
                throw S3Errors.NoSuchVersion(key, versionId);
            }

            if (version.IsDeleteMarker)
            {
                throw S3Errors.MethodNotAllowed(Request?.Method ?? "GET", "DeleteMarker")
                    .WithHeader("Allow", "DELETE")
                    .WithHeader("x-amz-delete-marker", "true")
                    .WithHeader("x-amz-version-id", version.VersionId)
                    .WithHeader("Last-Modified", Http.S3Wire.FormatTimestamp(version.LastModified, "rfc822"));
            }

            return version;
        }

        version = bucket.Current(key);
        if (version is null)
        {
            throw S3Errors.NoSuchKey(key);
        }

        if (version.IsDeleteMarker)
        {
            throw S3Errors.NoSuchKey(key)
                .WithHeader("x-amz-delete-marker", "true")
                .WithHeader("x-amz-version-id", version.VersionId);
        }

        return version;
    }

    /// <summary>
    /// Fills a GetObject or HeadObject answer for <paramref name="version"/>, after the request's
    /// conditions hold, and returns the range of the data it answers (a part, a byte range, or all).
    /// </summary>
    private (long Offset, long Length) Describe(S3Bucket bucket, S3ObjectVersion version, IS3ObjectRead request, IS3ObjectHeaders output)
    {
        CheckReadConditions(version, request);
        CheckSseCustomerKey(version, request);

        long offset = 0;
        var length = version.Size;
        var partial = false;
        if (request.PartNumber is { } partNumber)
        {
            if (request.Range is not null)
            {
                throw S3Errors.InvalidRequest("Cannot specify both Range header and partNumber query parameter");
            }

            var partCount = version.Parts?.Count ?? 1;
            if (partNumber < 1 || partNumber > 10000)
            {
                throw S3Errors.InvalidArgument("Part number must be an integer between 1 and 10000, inclusive", "partNumber", partNumber.ToString(CultureInfo.InvariantCulture));
            }

            if (partNumber > partCount)
            {
                throw new S3Exception("InvalidPartNumber", "The requested partnumber is not satisfiable", HttpStatusCode.RequestedRangeNotSatisfiable,
                    ("PartNumberRequested", partNumber.ToString(CultureInfo.InvariantCulture)), ("ActualPartCount", partCount.ToString(CultureInfo.InvariantCulture)));
            }

            if (version.Parts is { } parts)
            {
                offset = parts[partNumber - 1].Offset;
                length = parts[partNumber - 1].Size;
                output.PartsCount = parts.Count;
                partial = true;
            }
        }
        else if (ParseRange(request.Range, version.Size) is { } range)
        {
            offset = range.Offset;
            length = range.Length;
            partial = true;
        }

        if (partial)
        {
            output.ContentRange = length == 0
                ? $"bytes */{Invariant(version.Size)}"
                : $"bytes {Invariant(offset)}-{Invariant(offset + length - 1)}/{Invariant(version.Size)}";
        }

        output.AcceptRanges = "bytes";
        output.LastModified = version.LastModified;
        output.ContentLength = length;
        output.ETag = version.ETag;
        output.VersionId = VersionIdHeader(bucket, version);
        output.CacheControl = request.ResponseCacheControl ?? version.CacheControl;
        output.ContentDisposition = request.ResponseContentDisposition ?? version.ContentDisposition;
        output.ContentEncoding = request.ResponseContentEncoding ?? version.ContentEncoding;
        output.ContentLanguage = request.ResponseContentLanguage ?? version.ContentLanguage;
        output.ContentType = request.ResponseContentType ?? version.ContentType;
        output.Expires = request.ResponseExpires is { } expires ? Http.S3Wire.FormatTimestamp(expires, "rfc822") : version.Expires;
        output.WebsiteRedirectLocation = version.WebsiteRedirectLocation;
        output.ServerSideEncryption = version.SseCustomerKeyMd5 is null ? version.ServerSideEncryption : null;
        output.SSEKMSKeyId = version.SseKmsKeyId;
        output.BucketKeyEnabled = version.BucketKeyEnabled;
        output.SSECustomerAlgorithm = version.SseCustomerKeyMd5 is null ? null : "AES256";
        output.SSECustomerKeyMD5 = version.SseCustomerKeyMd5;
        output.Metadata = version.Metadata.Count == 0 ? null : new Dictionary<string, string>(version.Metadata, StringComparer.Ordinal);
        output.StorageClass = version.StorageClass == "STANDARD" ? null : version.StorageClass;
        output.TagCount = version.Tags.Count == 0 ? null : version.Tags.Count;
        output.ReplicationStatus = version.ReplicationStatus;
        output.Restore = RestoreHeader(version);
        if (request.ChecksumMode == "ENABLED" && version.Checksum is not null && !partial)
        {
            SetChecksum(output, version.ChecksumAlgorithm, version.Checksum);
            output.ChecksumType = version.ChecksumType;
        }
        else if (request.ChecksumMode == "ENABLED" && partial && request.PartNumber is not null && version.Parts is { } checkedParts
            && checkedParts[request.PartNumber.Value - 1].Checksum is { } partChecksum && version.ChecksumType == "COMPOSITE")
        {
            SetChecksum(output, version.ChecksumAlgorithm, partChecksum);
            output.ChecksumType = version.ChecksumType;
        }

        output.Expiration = LifecycleExpiration(bucket, version);
        DescribeMore(bucket, version, output);
        return (offset, length);
    }

    /// <summary>Adds what other areas (Object Lock, replication, restore) answer about an object.</summary>
    partial void DescribeMore(S3Bucket bucket, S3ObjectVersion version, IS3ObjectHeaders output);

    /// <summary>Checks an SSE-C object is read with its key; answers nothing for other objects.</summary>
    partial void CheckSseCustomerKey(S3ObjectVersion version, IS3ObjectRead request);

    /// <summary>
    /// GetObject's and HeadObject's conditions, as S3 evaluates them: a failed <c>If-Match</c> (or, without
    /// one, a failed <c>If-Unmodified-Since</c>) is 412; a matching <c>If-None-Match</c> (or, without one,
    /// an unmet <c>If-Modified-Since</c>) is 304.
    /// </summary>
    private static void CheckReadConditions(S3ObjectVersion version, IS3ObjectRead request)
    {
        if (request.IfMatch is { } ifMatch && !ETagMatches(ifMatch, version.ETag))
        {
            throw S3Errors.PreconditionFailed("If-Match");
        }

        if (request.IfMatch is null && request.IfUnmodifiedSince is { } unmodifiedSince && version.LastModified > unmodifiedSince)
        {
            throw S3Errors.PreconditionFailed("If-Unmodified-Since");
        }

        if ((request.IfNoneMatch is { } ifNoneMatch && ETagMatches(ifNoneMatch, version.ETag))
            || (request.IfNoneMatch is null && request.IfModifiedSince is { } modifiedSince && version.LastModified <= modifiedSince))
        {
            throw S3Errors.NotModified()
                .WithHeader("ETag", version.ETag)
                .WithHeader("Last-Modified", Http.S3Wire.FormatTimestamp(version.LastModified, "rfc822"));
        }
    }

    /// <summary>
    /// A conditional write's preconditions: <c>If-None-Match: *</c> fails when the key has a current
    /// object, <c>If-Match</c> when it has none (<c>NoSuchKey</c>) or another ETag.
    /// </summary>
    private static void CheckWriteConditions(S3Bucket bucket, string key, string? ifMatch, string? ifNoneMatch)
    {
        var current = bucket.Current(key);
        var exists = current is { IsDeleteMarker: false };
        if (ifNoneMatch is not null)
        {
            if (ifNoneMatch.Trim() != "*")
            {
                throw new S3Exception("NotImplemented", "A header you provided implies functionality that is not implemented", HttpStatusCode.NotImplemented, ("Header", "If-None-Match"));
            }

            if (exists)
            {
                throw S3Errors.PreconditionFailed("If-None-Match");
            }
        }

        if (ifMatch is not null)
        {
            if (!exists)
            {
                throw S3Errors.NoSuchKey(key);
            }

            if (!ETagMatches(ifMatch, current!.ETag))
            {
                throw S3Errors.PreconditionFailed("If-Match");
            }
        }
    }

    /// <summary>The copy source's conditions: a failed match is 412, as is a met none-match.</summary>
    private static void CheckCopySourceConditions(S3ObjectVersion source, string? ifMatch, string? ifNoneMatch, DateTime? ifModifiedSince, DateTime? ifUnmodifiedSince)
    {
        if (ifMatch is not null && !ETagMatches(ifMatch, source.ETag))
        {
            throw S3Errors.PreconditionFailed("x-amz-copy-source-If-Match");
        }

        if (ifMatch is null && ifUnmodifiedSince is { } unmodified && source.LastModified > unmodified)
        {
            throw S3Errors.PreconditionFailed("x-amz-copy-source-If-Unmodified-Since");
        }

        if (ifNoneMatch is not null && ETagMatches(ifNoneMatch, source.ETag))
        {
            throw S3Errors.PreconditionFailed("x-amz-copy-source-If-None-Match");
        }

        if (ifNoneMatch is null && ifModifiedSince is { } modified && source.LastModified <= modified)
        {
            throw S3Errors.PreconditionFailed("x-amz-copy-source-If-Modified-Since");
        }
    }

    /// <summary>Whether a conditional header's ETags (a list, or <c>*</c>) include <paramref name="etag"/>.</summary>
    internal static bool ETagMatches(string condition, string etag)
    {
        var bare = etag.Trim('"');
        foreach (var part in condition.Split(','))
        {
            var candidate = part.Trim();
            if (candidate == "*" || candidate.Trim('"') == bare || (candidate.StartsWith("W/", StringComparison.Ordinal) && candidate.Substring(2).Trim('"') == bare))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A <c>Range: bytes=...</c> header's one range, clamped to the object; null for no range, or one
    /// S3 ignores (malformed, or several ranges). A range that starts past the end is <c>InvalidRange</c>.
    /// </summary>
    private static (long Offset, long Length)? ParseRange(string? range, long size)
    {
        if (range is null)
        {
            return null;
        }

        var text = range.Trim();
        if (!text.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || text.Contains(',', StringComparison.Ordinal))
        {
            return null;
        }

        var spec = text.Substring(6).Trim();
        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            return null;
        }

        var startText = spec.Substring(0, dash).Trim();
        var endText = spec.Substring(dash + 1).Trim();
        if (startText.Length == 0)
        {
            // A suffix: the last n bytes.
            if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix))
            {
                return null;
            }

            if (suffix == 0 || size == 0)
            {
                throw S3Errors.InvalidRange(range, size);
            }

            var length = Math.Min(suffix, size);
            return (size - length, length);
        }

        if (!long.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out var start))
        {
            return null;
        }

        long end;
        if (endText.Length == 0)
        {
            end = size - 1;
        }
        else if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start)
        {
            return null;
        }

        if (start >= size)
        {
            throw S3Errors.InvalidRange(range, size);
        }

        end = Math.Min(end, size - 1);
        return (start, end - start + 1);
    }

    // ------------------------------------------------------------------------------------------
    // Validation
    // ------------------------------------------------------------------------------------------

    /// <summary>An object key: 1 to 1024 bytes of UTF-8.</summary>
    private static string CheckKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw S3Errors.InvalidArgument("Object key must not be empty", "key", key);
        }

        if (System.Text.Encoding.UTF8.GetByteCount(key!) > 1024)
        {
            throw new S3Exception("KeyTooLongError", "Your key is too long", HttpStatusCode.BadRequest,
                ("MaxSizeAllowed", "1024"), ("Size", System.Text.Encoding.UTF8.GetByteCount(key!).ToString(CultureInfo.InvariantCulture)));
        }

        return key!;
    }

    /// <summary>A version id S3 could have minted (or <c>null</c>); anything else is <c>InvalidArgument</c>.</summary>
    private static void CheckVersionIdFormat(string versionId)
    {
        if (versionId == S3ObjectVersion.NullVersionId)
        {
            return;
        }

        if (versionId.Length == 0 || versionId.Length > 1024 || versionId.Any(c => !(char.IsLetterOrDigit(c) && c < 128) && c is not ('.' or '_' or '-'))
            || !S3VersionIds.IsIntact(versionId))
        {
            throw S3Errors.InvalidArgument("Invalid version id specified", "versionId", versionId);
        }
    }

    /// <summary>User metadata is limited to 2 KB: names and values, in UTF-8.</summary>
    private static void CheckMetadataSize(Dictionary<string, string> metadata)
    {
        var size = metadata.Sum(m => System.Text.Encoding.UTF8.GetByteCount(m.Key) + System.Text.Encoding.UTF8.GetByteCount(m.Value));
        if (size > 2048)
        {
            throw new S3Exception("MetadataTooLarge", "Your metadata headers exceed the maximum allowed metadata size", HttpStatusCode.BadRequest,
                ("Size", size.ToString(CultureInfo.InvariantCulture)), ("MaxSizeAllowed", "2048"));
        }
    }

    private static readonly HashSet<string> StorageClasses = new(StringComparer.Ordinal)
    {
        "STANDARD", "REDUCED_REDUNDANCY", "STANDARD_IA", "ONEZONE_IA", "INTELLIGENT_TIERING", "GLACIER",
        "DEEP_ARCHIVE", "OUTPOSTS", "GLACIER_IR", "SNOW", "EXPRESS_ONEZONE", "FSX_OPENZFS", "FSX_ONTAP",
    };

    /// <summary>
    /// A write's storage class: <c>STANDARD</c> when none is given. A class S3 doesn't have, or one a
    /// general purpose bucket can't hold (S3 Express One Zone, Outposts, Snow, FSx), is
    /// <c>InvalidStorageClass</c>.
    /// </summary>
    private string CheckStorageClass(string? storageClass)
    {
        var directoryBucket = Request?.Bucket?.EndsWith("--x-s3", StringComparison.Ordinal) ?? false;
        if (storageClass is null)
        {
            return directoryBucket ? "EXPRESS_ONEZONE" : "STANDARD";
        }

        var valid = StorageClasses.Contains(storageClass)
            && (directoryBucket || storageClass is not ("EXPRESS_ONEZONE" or "OUTPOSTS" or "SNOW" or "FSX_OPENZFS" or "FSX_ONTAP"));
        return valid
            ? storageClass
            : throw new S3Exception("InvalidStorageClass", "The storage class you specified is not valid", HttpStatusCode.BadRequest,
                ("StorageClassRequested", storageClass));
    }

    /// <summary><c>Content-MD5</c>: the base64 of 16 bytes (else <c>InvalidDigest</c>) matching the body (else <c>BadDigest</c>).</summary>
    private static void VerifyContentMd5(string? contentMd5, ReadOnlySpan<byte> data)
    {
        if (contentMd5 is null)
        {
            return;
        }

        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(contentMd5.Trim());
        }
        catch (FormatException)
        {
            throw S3Errors.InvalidDigest();
        }

        if (expected.Length != 16)
        {
            throw S3Errors.InvalidDigest();
        }

        if (!expected.AsSpan().SequenceEqual(S3Checksums.Md5(data)))
        {
            throw S3Errors.BadDigest();
        }
    }

    /// <summary>
    /// The checksum a request sent (one <c>x-amz-checksum-*</c> header), checked against the data, or
    /// the one <c>x-amz-sdk-checksum-algorithm</c> names, computed. Returns nulls when it sent neither.
    /// </summary>
    private static (string? Algorithm, string? Value) VerifyChecksums(IS3ChecksumMembers given, string? sdkAlgorithm, ReadOnlySpan<byte> data)
    {
        string? algorithm = null;
        string? value = null;
        foreach (var candidate in S3Checksums.Algorithms)
        {
            var sent = GetChecksum(given, candidate);
            if (sent is null)
            {
                continue;
            }

            if (algorithm is not null)
            {
                throw S3Errors.InvalidRequest("Expecting a single x-amz-checksum- header. Multiple checksum Types are not allowed.");
            }

            var expected = S3Checksums.ComputeBytes(candidate, data);
            byte[] sentBytes;
            try
            {
                sentBytes = Convert.FromBase64String(sent);
            }
            catch (FormatException)
            {
                throw S3Errors.InvalidRequest($"Value for {S3Checksums.HeaderName(candidate)} header is invalid.");
            }

            if (sentBytes.Length != expected.Length)
            {
                throw S3Errors.InvalidRequest($"Value for {S3Checksums.HeaderName(candidate)} header is invalid.");
            }

            if (!sentBytes.AsSpan().SequenceEqual(expected))
            {
                throw S3Errors.BadDigest($"The {candidate} you specified did not match the calculated checksum.");
            }

            algorithm = candidate;
            value = sent;
        }

        if (algorithm is null && sdkAlgorithm is not null)
        {
            algorithm = S3Checksums.Normalize(sdkAlgorithm)
                ?? throw S3Errors.InvalidRequest("Checksum algorithm provided is unsupported. Please try again with any of the valid types: [CRC32, CRC32C, CRC64NVME, SHA1, SHA256]");
            value = S3Checksums.Compute(algorithm, data);
        }

        return (algorithm, value);
    }

    internal static string? GetChecksum(IS3ChecksumMembers members, string algorithm) => algorithm switch
    {
        "CRC32" => members.ChecksumCRC32,
        "CRC32C" => members.ChecksumCRC32C,
        "CRC64NVME" => members.ChecksumCRC64NVME,
        "SHA1" => members.ChecksumSHA1,
        "SHA256" => members.ChecksumSHA256,
        _ => null,
    };

    internal static void SetChecksum(IS3ChecksumMembers members, string? algorithm, string? value)
    {
        switch (algorithm)
        {
            case "CRC32": members.ChecksumCRC32 = value; break;
            case "CRC32C": members.ChecksumCRC32C = value; break;
            case "CRC64NVME": members.ChecksumCRC64NVME = value; break;
            case "SHA1": members.ChecksumSHA1 = value; break;
            case "SHA256": members.ChecksumSHA256 = value; break;
        }
    }

    /// <summary>
    /// The tags of an <c>x-amz-tagging</c> header (<c>key1=value1&amp;key2=value2</c>, URL-encoded),
    /// checked as tags are.
    /// </summary>
    private static List<Tag> ParseTagHeader(string? header)
    {
        var tags = new List<Tag>();
        if (string.IsNullOrEmpty(header))
        {
            return tags;
        }

        foreach (var pair in header!.Split('&'))
        {
            if (pair.Length == 0)
            {
                continue;
            }

            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Http.S3Addressing.PercentDecode((eq < 0 ? pair : pair.Substring(0, eq)).Replace('+', ' '));
            var value = eq < 0 ? string.Empty : Http.S3Addressing.PercentDecode(pair.Substring(eq + 1).Replace('+', ' '));
            tags.Add(new Tag { Key = key, Value = value });
        }

        CheckObjectTags(tags);
        return tags;
    }

    /// <summary>The bucket a copy source names: <c>NoSuchBucket</c> when there is none.</summary>
    private S3Bucket GetSourceBucket(string name) =>
        Store.Buckets.TryGetValue(name, out var bucket) ? bucket : throw S3Errors.NoSuchBucket(name);

    /// <summary>The version a copy reads: the current one, or the one the source names.</summary>
    private static S3ObjectVersion SourceVersion(S3Bucket bucket, string key, string? versionId)
    {
        if (versionId is not null)
        {
            CheckVersionIdFormat(versionId);
            var version = bucket.Version(key, versionId) ?? throw S3Errors.NoSuchVersion(key, versionId);
            if (version.IsDeleteMarker)
            {
                throw S3Errors.InvalidRequest("The source of a copy request may not specifically refer to a delete marker by version id.");
            }

            return version;
        }

        var current = bucket.Current(key);
        if (current is null || current.IsDeleteMarker)
        {
            throw S3Errors.NoSuchKey(key);
        }

        return current;
    }

    /// <summary>
    /// <c>x-amz-copy-source</c>: <c>bucket/key</c>, URL-encoded, with an optional leading slash and
    /// <c>?versionId=</c>.
    /// </summary>
    private static (string Bucket, string Key, string? VersionId) ParseCopySource(string? copySource)
    {
        if (string.IsNullOrEmpty(copySource))
        {
            throw S3Errors.InvalidArgument("Copy Source must mention the source bucket and key: sourcebucket/sourcekey", "x-amz-copy-source", copySource);
        }

        var text = copySource!;
        string? versionId = null;
        var question = text.IndexOf("?versionId=", StringComparison.Ordinal);
        if (question >= 0)
        {
            versionId = Http.S3Addressing.PercentDecode(text.Substring(question + "?versionId=".Length));
            text = text.Substring(0, question);
        }

        text = Http.S3Addressing.PercentDecode(text).TrimStart('/');
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == text.Length - 1)
        {
            throw S3Errors.InvalidArgument("Copy Source must mention the source bucket and key: sourcebucket/sourcekey", "x-amz-copy-source", copySource);
        }

        return (text.Substring(0, slash), text.Substring(slash + 1), versionId);
    }
}
