#pragma warning disable CA1308 // S3 writes metadata names and checksum algorithms in lower case.
using System.Globalization;
using System.Net;
using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

internal sealed partial class InternalS3Client
{
    /// <summary>Every part but the last must be at least 5 MiB.</summary>
    private const long MinimumPartSize = 5L * 1024 * 1024;

    /// <summary>
    /// Starts a multipart upload, keeping what the object will get (content headers, metadata, tags,
    /// storage class, encryption) and its checksum algorithm and type.
    /// </summary>
    public override Task<CreateMultipartUploadOutput> CreateMultipartUploadAsync(CreateMultipartUploadRequest request, CancellationToken cancellationToken)
    {
        var algorithm = request.ChecksumAlgorithm is null ? null : S3Checksums.Normalize(request.ChecksumAlgorithm)
            ?? throw S3Errors.InvalidRequest("Checksum algorithm provided is unsupported. Please try again with any of the valid types: [CRC32, CRC32C, CRC64NVME, SHA1, SHA256]");
        var checksumType = request.ChecksumType;
        if (checksumType is not (null or "COMPOSITE" or "FULL_OBJECT"))
        {
            throw S3Errors.InvalidRequest("Value for x-amz-checksum-type header is invalid.");
        }

        if (checksumType is not null && algorithm is null)
        {
            throw S3Errors.InvalidRequest("The x-amz-checksum-type header can only be used with the x-amz-checksum-algorithm header.");
        }

        if (algorithm is not null)
        {
            checksumType ??= algorithm == "CRC64NVME" ? "FULL_OBJECT" : "COMPOSITE";
            if (checksumType == "FULL_OBJECT" && !S3Checksums.IsCrc(algorithm))
            {
                throw S3Errors.InvalidRequest($"The FULL_OBJECT checksum type cannot be used with the {algorithm.ToLowerInvariant()} checksum algorithm.");
            }

            if (checksumType == "COMPOSITE" && algorithm == "CRC64NVME")
            {
                throw S3Errors.InvalidRequest("The COMPOSITE checksum type cannot be used with the crc64nvme checksum algorithm.");
            }
        }

        var metadata = request.Metadata ?? new Dictionary<string, string>(StringComparer.Ordinal);
        CheckMetadataSize(metadata);
        var tags = ParseTagHeader(request.Tagging);
        var storageClass = CheckStorageClass(request.StorageClass);
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var key = CheckKey(request.Key);
            var upload = new S3MultipartUpload
            {
                UploadId = NewUploadId(),
                Key = key,
                Initiated = Now,
                ContentType = request.ContentType ?? "binary/octet-stream",
                ContentEncoding = request.ContentEncoding,
                ContentDisposition = request.ContentDisposition,
                ContentLanguage = request.ContentLanguage,
                CacheControl = request.CacheControl,
                Expires = request.Expires,
                WebsiteRedirectLocation = request.WebsiteRedirectLocation,
                Metadata = metadata,
                StorageClass = storageClass,
                ServerSideEncryption = request.ServerSideEncryption ?? "AES256",
                SseKmsKeyId = request.SSEKMSKeyId,
                BucketKeyEnabled = request.BucketKeyEnabled,
                SseCustomerKeyMd5 = request.SSECustomerKeyMD5,
                Tags = tags,
                ChecksumAlgorithm = algorithm,
                ChecksumType = algorithm is null ? null : checksumType,
                OwnerId = OwnerId,
            };
            OnUploadCreated(bucket, upload, request);
            ApplyAbortRule(bucket, upload);
            bucket.Uploads.Add(upload.UploadId, upload);
            return Task.FromResult(new CreateMultipartUploadOutput
            {
                Bucket = bucket.Name,
                Key = key,
                UploadId = upload.UploadId,
                ServerSideEncryption = upload.SseCustomerKeyMd5 is null ? upload.ServerSideEncryption : null,
                SSEKMSKeyId = upload.SseKmsKeyId,
                BucketKeyEnabled = upload.BucketKeyEnabled,
                SSECustomerAlgorithm = request.SSECustomerAlgorithm,
                SSECustomerKeyMD5 = request.SSECustomerKeyMD5,
                ChecksumAlgorithm = upload.ChecksumAlgorithm,
                ChecksumType = upload.ChecksumType,
                AbortDate = upload.AbortDate,
                AbortRuleId = upload.AbortRuleId,
            });
        }
    }

    /// <summary>Applies the rest of a CreateMultipartUpload request (ACL, Object Lock) to the upload.</summary>
    partial void OnUploadCreated(S3Bucket bucket, S3MultipartUpload upload, CreateMultipartUploadRequest request);

    /// <summary>Uploads (or replaces) one part, checking its digests as PutObject does.</summary>
    public override Task<UploadPartOutput> UploadPartAsync(UploadPartRequest request, CancellationToken cancellationToken)
    {
        var partNumber = CheckPartNumber(request.PartNumber);
        var data = request.Body?.ToArray() ?? [];
        VerifyContentMd5(request.ContentMD5, data);
        var (algorithm, checksum) = VerifyChecksums(request, request.ChecksumAlgorithm, data);
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var upload = GetUpload(bucket, request.Key, request.UploadId);
            if (upload.ChecksumAlgorithm is { } expected && algorithm is not null && algorithm != expected)
            {
                throw S3Errors.InvalidRequest($"Checksum Type mismatch occurred, expected checksum Type: {expected.ToLowerInvariant()}, actual checksum Type: {algorithm.ToLowerInvariant()}");
            }

            var part = StorePart(upload, partNumber, data, algorithm, checksum);
            var output = new UploadPartOutput
            {
                ETag = part.ETag,
                ServerSideEncryption = upload.SseCustomerKeyMd5 is null ? upload.ServerSideEncryption : null,
                SSEKMSKeyId = upload.SseKmsKeyId,
                BucketKeyEnabled = upload.BucketKeyEnabled,
                SSECustomerAlgorithm = upload.SseCustomerKeyMd5 is null ? null : "AES256",
                SSECustomerKeyMD5 = upload.SseCustomerKeyMd5,
            };
            if (algorithm is not null)
            {
                SetChecksum(output, algorithm, checksum);
            }

            return Task.FromResult(output);
        }
    }

    /// <summary>Uploads one part from (a range of) an existing object.</summary>
    public override Task<UploadPartCopyOutput> UploadPartCopyAsync(UploadPartCopyRequest request, CancellationToken cancellationToken)
    {
        var partNumber = CheckPartNumber(request.PartNumber);
        var (sourceBucketName, sourceKey, sourceVersionId) = ParseCopySource(request.CopySource);
        lock (Store.Gate)
        {
            var sourceBucket = GetSourceBucket(sourceBucketName);
            var source = SourceVersion(sourceBucket, sourceKey, sourceVersionId);
            CheckCopySourceConditions(source, request.CopySourceIfMatch, request.CopySourceIfNoneMatch, request.CopySourceIfModifiedSince, request.CopySourceIfUnmodifiedSince);
            var bucket = GetBucket(request.Bucket);
            var upload = GetUpload(bucket, request.Key, request.UploadId);
            var data = source.Data;
            if (request.CopySourceRange is { } range)
            {
                var (offset, length) = ParseCopySourceRange(range, source.Size);
                data = data.AsSpan((int)offset, (int)length).ToArray();
            }

            var algorithm = upload.ChecksumAlgorithm;
            var part = StorePart(upload, partNumber, data, algorithm, algorithm is null ? null : S3Checksums.Compute(algorithm, data));
            var result = new CopyPartResult { ETag = part.ETag, LastModified = part.LastModified };
            if (algorithm is not null)
            {
                SetChecksum(result, algorithm, part.Checksums[algorithm]);
            }

            return Task.FromResult(new UploadPartCopyOutput
            {
                CopyPartResult = result,
                CopySourceVersionId = sourceBucket.Versioning is null ? null : source.VersionId,
                ServerSideEncryption = upload.ServerSideEncryption,
            });
        }
    }

    /// <summary>
    /// Completes an upload from the parts the request lists, which must be in ascending order, each
    /// uploaded with the ETag given, and each but the last at least 5 MiB. The object's ETag is the MD5
    /// of the parts' MD5s and the part count; its checksum is the upload's algorithm over the whole
    /// object (<c>FULL_OBJECT</c>) or over the parts' checksums (<c>COMPOSITE</c>).
    /// </summary>
    public override Task<CompleteMultipartUploadOutput> CompleteMultipartUploadAsync(CompleteMultipartUploadRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var key = CheckKey(request.Key);
            if (request.UploadId is { } completedId && bucket.CompletedUploads.Contains(completedId) && !bucket.Uploads.ContainsKey(completedId))
            {
                throw S3Errors.NoSuchUpload(completedId);
            }

            var upload = GetUpload(bucket, key, request.UploadId);
            var listed = request.MultipartUpload?.Parts;
            if (listed is null || listed.Count == 0)
            {
                throw new S3Exception("MalformedXML", "The XML you provided was not well-formed or did not validate against our published schema", HttpStatusCode.BadRequest);
            }

            var parts = new List<S3UploadedPart>();
            var previous = 0;
            foreach (var item in listed)
            {
                var number = item.PartNumber ?? throw S3Errors.MalformedXml();
                if (number <= previous)
                {
                    throw S3Errors.InvalidPartOrder(upload.UploadId);
                }

                previous = number;
                if (!upload.Parts.TryGetValue(number, out var part) || item.ETag is null || !ETagMatches(item.ETag, part.ETag))
                {
                    throw S3Errors.InvalidPart(upload.UploadId, number, item.ETag);
                }

                foreach (var algorithm in S3Checksums.Algorithms)
                {
                    if (GetChecksum(item, algorithm) is { } given
                        && (!part.Checksums.TryGetValue(algorithm, out var stored) || stored != given))
                    {
                        throw S3Errors.InvalidPart(upload.UploadId, number, item.ETag);
                    }
                }

                parts.Add(part);
            }

            for (var i = 0; i < parts.Count - 1; i++)
            {
                if (parts[i].Data.LongLength < MinimumPartSize)
                {
                    throw S3Errors.EntityTooSmall(parts[i].Data.LongLength, parts[i].PartNumber, parts[i].ETag);
                }
            }

            CheckWriteConditions(bucket, key, request.IfMatch, request.IfNoneMatch);

            var data = new byte[parts.Sum(p => p.Data.LongLength)];
            var storedParts = new List<S3StoredPart>(parts.Count);
            long offset = 0;
            foreach (var part in parts)
            {
                Buffer.BlockCopy(part.Data, 0, data, (int)offset, part.Data.Length);
                storedParts.Add(new S3StoredPart
                {
                    PartNumber = part.PartNumber,
                    Offset = offset,
                    Size = part.Data.LongLength,
                    ETag = part.ETag,
                    Checksum = upload.ChecksumAlgorithm is { } a && part.Checksums.TryGetValue(a, out var c) ? c : null,
                });
                offset += part.Data.LongLength;
            }

            if (request.MpuObjectSize is { } declaredSize && declaredSize != data.LongLength)
            {
                throw S3Errors.InvalidRequest($"The provided 'x-amz-mp-object-size' header value {declaredSize.ToString(CultureInfo.InvariantCulture)} does not match what was computed: {data.LongLength.ToString(CultureInfo.InvariantCulture)}");
            }

            var (checksumAlgorithm, checksum, checksumType) = CompletedChecksum(upload, storedParts, data, request);
            var version = new S3ObjectVersion
            {
                Key = key,
                VersionId = NextVersionId(bucket),
                Data = data,
                ETag = bucket.IsDirectory ? DirectoryMultipartETag(data) : S3Checksums.MultipartETag(parts.Select(p => p.ETag).ToList()),
                LastModified = NowToTheSecond,
                ContentType = upload.ContentType,
                ContentEncoding = upload.ContentEncoding,
                ContentDisposition = upload.ContentDisposition,
                ContentLanguage = upload.ContentLanguage,
                CacheControl = upload.CacheControl,
                Expires = upload.Expires,
                WebsiteRedirectLocation = upload.WebsiteRedirectLocation,
                Metadata = upload.Metadata,
                StorageClass = upload.StorageClass,
                ServerSideEncryption = upload.ServerSideEncryption,
                SseKmsKeyId = upload.SseKmsKeyId,
                BucketKeyEnabled = upload.BucketKeyEnabled,
                SseCustomerKeyMd5 = upload.SseCustomerKeyMd5,
                ChecksumAlgorithm = checksumAlgorithm,
                Checksum = checksum,
                ChecksumType = checksumType,
                Parts = storedParts,
                Tags = upload.Tags,
                OwnerId = upload.OwnerId,
            };
            OnUploadCompleting(bucket, upload, version);
            bucket.Uploads.Remove(upload.UploadId);
            bucket.CompletedUploads.Add(upload.UploadId);
            bucket.Add(version);
            OnObjectWritten(bucket, version, "ObjectCreated:CompleteMultipartUpload");
            ReplicateWrite(bucket, version);
            var output = new CompleteMultipartUploadOutput
            {
                Location = $"https://{bucket.Name}.s3.{bucket.Region}.amazonaws.com/{Uri.EscapeDataString(key).Replace("%2F", "/", StringComparison.Ordinal)}",
                Bucket = bucket.Name,
                Key = key,
                ETag = version.ETag,
                VersionId = VersionIdHeader(bucket, version),
                ServerSideEncryption = version.SseCustomerKeyMd5 is null ? version.ServerSideEncryption : null,
                SSEKMSKeyId = version.SseKmsKeyId,
                BucketKeyEnabled = version.BucketKeyEnabled,
                ChecksumType = upload.ChecksumAlgorithm is null && request.ChecksumType is null ? checksumType : checksumType,
            };
            SetChecksum(output, checksumAlgorithm, checksum);
            return Task.FromResult(output);
        }
    }

    /// <summary>Applies what other areas keep on an upload (ACL, Object Lock) to the object it completes into.</summary>
    partial void OnUploadCompleting(S3Bucket bucket, S3MultipartUpload upload, S3ObjectVersion version);

    /// <summary>
    /// The completed object's checksum: the upload's algorithm over the parts' checksums
    /// (<c>COMPOSITE</c>, <c>-N</c>) or over the whole object (<c>FULL_OBJECT</c>); with no algorithm,
    /// CRC64NVME over the whole object. A full-object checksum the request sends must match.
    /// </summary>
    private static (string Algorithm, string Value, string Type) CompletedChecksum(
        S3MultipartUpload upload, List<S3StoredPart> parts, byte[] data, CompleteMultipartUploadRequest request)
    {
        var algorithm = upload.ChecksumAlgorithm ?? "CRC64NVME";
        var type = upload.ChecksumType ?? "FULL_OBJECT";
        if (request.ChecksumType is { } requestedType && upload.ChecksumType is not null && requestedType != upload.ChecksumType)
        {
            throw S3Errors.InvalidRequest($"The upload was created using the {upload.ChecksumType} checksum mode. The complete request must use the same checksum mode.");
        }

        string value;
        if (type == "COMPOSITE")
        {
            var partChecksums = parts.Select(p => p.Checksum ?? throw S3Errors.InvalidRequest(
                $"The upload was created using a {algorithm.ToLowerInvariant()} checksum. The complete request must include the checksum for each part. It was missing for part {p.PartNumber.ToString(CultureInfo.InvariantCulture)} in the request.")).ToList();
            value = S3Checksums.Composite(algorithm, partChecksums);
        }
        else
        {
            value = S3Checksums.Compute(algorithm, data);
        }

        if (GetChecksum(request, algorithm) is { } given && given != value && type == "FULL_OBJECT")
        {
            throw S3Errors.BadDigest($"The {algorithm.ToLowerInvariant()} you specified did not match the calculated checksum.");
        }

        return (algorithm, value, type);
    }

    /// <summary>
    /// Aborts an upload and drops its parts. An upload completed or aborted before is answered as
    /// aborted again; any other unknown upload is <c>NoSuchUpload</c>.
    /// </summary>
    public override Task<AbortMultipartUploadOutput> AbortMultipartUploadAsync(AbortMultipartUploadRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            if (request.UploadId is { } uploadId && (bucket.CompletedUploads.Contains(uploadId) || bucket.AbortedUploads.Contains(uploadId)))
            {
                return Task.FromResult(new AbortMultipartUploadOutput());
            }

            var upload = GetUpload(bucket, request.Key, request.UploadId);
            if (request.IfMatchInitiatedTime is { } initiated && Math.Abs((initiated - upload.Initiated).TotalSeconds) >= 1)
            {
                throw S3Errors.PreconditionFailed("x-amz-if-match-initiated-time");
            }

            bucket.Uploads.Remove(upload.UploadId);
            bucket.AbortedUploads.Add(upload.UploadId);
            return Task.FromResult(new AbortMultipartUploadOutput());
        }
    }

    /// <summary>The uploads in progress, by key and then by when each was started, paged by key and upload-id markers.</summary>
    public override Task<ListMultipartUploadsOutput> ListMultipartUploadsAsync(ListMultipartUploadsRequest request, CancellationToken cancellationToken)
    {
        var maxUploads = request.MaxUploads ?? 1000;
        if (maxUploads < 0)
        {
            throw S3Errors.InvalidArgument("Argument max-uploads must be an integer between 0 and 2147483647", "max-uploads", maxUploads.ToString(CultureInfo.InvariantCulture));
        }

        maxUploads = Math.Min(maxUploads, 1000);
        var encode = CheckEncodingType(request.EncodingType);
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var output = new ListMultipartUploadsOutput
            {
                Bucket = bucket.Name,
                Prefix = Encode(request.Prefix ?? string.Empty, encode),
                KeyMarker = Encode(request.KeyMarker ?? string.Empty, encode),
                UploadIdMarker = request.UploadIdMarker ?? string.Empty,
                Delimiter = request.Delimiter is null ? null : Encode(request.Delimiter, encode),
                MaxUploads = maxUploads,
                EncodingType = request.EncodingType,
                IsTruncated = false,
            };

            var uploads = bucket.Uploads.Values
                .Where(u => request.Prefix is null || u.Key.StartsWith(request.Prefix, StringComparison.Ordinal))
                .OrderBy(u => u.Key, S3KeyComparer.Instance)
                .ThenBy(u => u.Initiated)
                .ThenBy(u => u.UploadId, StringComparer.Ordinal)
                .ToList();
            var count = 0;
            string? lastPrefix = null;
            S3MultipartUpload? last = null;
            var pastUploadMarker = string.IsNullOrEmpty(request.UploadIdMarker);
            foreach (var upload in uploads)
            {
                if (request.KeyMarker is { Length: > 0 } keyMarker)
                {
                    var comparison = S3KeyComparer.Instance.Compare(upload.Key, keyMarker);
                    if (comparison < 0 || (comparison == 0 && string.IsNullOrEmpty(request.UploadIdMarker)))
                    {
                        continue;
                    }

                    if (comparison == 0 && !pastUploadMarker)
                    {
                        if (upload.UploadId == request.UploadIdMarker)
                        {
                            pastUploadMarker = true;
                        }

                        continue;
                    }
                }

                var commonPrefix = CommonPrefixOf(upload.Key, request.Prefix, request.Delimiter);
                if (commonPrefix is not null && commonPrefix == lastPrefix)
                {
                    continue;
                }

                if (count == maxUploads)
                {
                    output.IsTruncated = true;
                    break;
                }

                count++;
                if (commonPrefix is not null)
                {
                    (output.CommonPrefixes ??= []).Add(new CommonPrefix { Prefix = Encode(commonPrefix, encode) });
                    lastPrefix = commonPrefix;
                    continue;
                }

                last = upload;
                (output.Uploads ??= []).Add(new MultipartUpload
                {
                    UploadId = upload.UploadId,
                    Key = Encode(upload.Key, encode),
                    Initiated = upload.Initiated,
                    StorageClass = upload.StorageClass,
                    Owner = Owner(upload.OwnerId),
                    Initiator = new Initiator { ID = upload.OwnerId },
                    ChecksumAlgorithm = upload.ChecksumAlgorithm,
                    ChecksumType = upload.ChecksumType,
                });
            }

            if (output.IsTruncated == true && last is not null)
            {
                output.NextKeyMarker = Encode(last.Key, encode);
                output.NextUploadIdMarker = last.UploadId;
            }

            return Task.FromResult(output);
        }
    }

    /// <summary>An upload's parts, in part-number order, paged by part-number marker.</summary>
    public override Task<ListPartsOutput> ListPartsAsync(ListPartsRequest request, CancellationToken cancellationToken)
    {
        var maxParts = request.MaxParts ?? 1000;
        if (maxParts < 0)
        {
            throw S3Errors.InvalidArgument("Argument max-parts must be an integer between 0 and 2147483647", "max-parts", maxParts.ToString(CultureInfo.InvariantCulture));
        }

        maxParts = Math.Min(maxParts, 1000);
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var upload = GetUpload(bucket, request.Key, request.UploadId);
            var marker = request.PartNumberMarker ?? 0;
            var remaining = upload.Parts.Values.Where(p => p.PartNumber > marker).ToList();
            var page = remaining.Take(maxParts).ToList();
            var output = new ListPartsOutput
            {
                Bucket = bucket.Name,
                Key = upload.Key,
                UploadId = upload.UploadId,
                PartNumberMarker = marker,
                MaxParts = maxParts,
                IsTruncated = remaining.Count > page.Count,
                NextPartNumberMarker = page.Count > 0 ? page[^1].PartNumber : null,
                Initiator = new Initiator { ID = upload.OwnerId },
                Owner = Owner(upload.OwnerId),
                StorageClass = upload.StorageClass,
                ChecksumAlgorithm = upload.ChecksumAlgorithm,
                ChecksumType = upload.ChecksumType,
                AbortDate = upload.AbortDate,
                AbortRuleId = upload.AbortRuleId,
            };
            foreach (var part in page)
            {
                var listed = new Part { PartNumber = part.PartNumber, LastModified = part.LastModified, ETag = part.ETag, Size = part.Data.LongLength };
                if (upload.ChecksumAlgorithm is { } algorithm && part.Checksums.TryGetValue(algorithm, out var checksum))
                {
                    SetChecksum(listed, algorithm, checksum);
                }

                (output.Parts ??= []).Add(listed);
            }

            return Task.FromResult(output);
        }
    }

    /// <summary>An upload in progress for the key; <c>NoSuchUpload</c> when the bucket has none by that id for that key.</summary>
    private static S3MultipartUpload GetUpload(S3Bucket bucket, string? key, string? uploadId)
    {
        if (uploadId is null || !bucket.Uploads.TryGetValue(uploadId, out var upload) || upload.Key != key)
        {
            throw S3Errors.NoSuchUpload(uploadId);
        }

        return upload;
    }

    private S3UploadedPart StorePart(S3MultipartUpload upload, int partNumber, byte[] data, string? algorithm, string? checksum)
    {
        var checksums = new Dictionary<string, string>(StringComparer.Ordinal);
        if (algorithm is not null && checksum is not null)
        {
            checksums[algorithm] = checksum;
        }

        if (upload.ChecksumAlgorithm is { } uploadAlgorithm && !checksums.ContainsKey(uploadAlgorithm))
        {
            checksums[uploadAlgorithm] = S3Checksums.Compute(uploadAlgorithm, data);
        }

        var part = new S3UploadedPart
        {
            PartNumber = partNumber,
            Data = data,
            ETag = S3Checksums.ETag(data),
            LastModified = NowToTheSecond,
            Checksums = checksums,
        };
        upload.Parts[partNumber] = part;
        return part;
    }

    private static int CheckPartNumber(int? partNumber)
    {
        if (partNumber is null or < 1 or > 10000)
        {
            throw S3Errors.InvalidArgument("Part number must be an integer between 1 and 10000, inclusive", "partNumber", partNumber?.ToString(CultureInfo.InvariantCulture));
        }

        return partNumber.Value;
    }

    /// <summary><c>x-amz-copy-source-range: bytes=first-last</c>, which must lie within the source.</summary>
    private static (long Offset, long Length) ParseCopySourceRange(string range, long size)
    {
        var text = range.Trim();
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        if (!text.StartsWith("bytes=", StringComparison.Ordinal) || dash < 0
            || !long.TryParse(text.AsSpan(6, dash - 6), NumberStyles.None, CultureInfo.InvariantCulture, out var first)
            || !long.TryParse(text.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var last)
            || last < first)
        {
            throw S3Errors.InvalidArgument("The x-amz-copy-source-range value must be of the form bytes=first-last where first and last are the zero-based offsets of the first and last bytes to copy", "x-amz-copy-source-range", range);
        }

        if (last >= size)
        {
            throw S3Errors.InvalidRequest($"The specified copy range is invalid for the source object size");
        }

        return (first, last - first + 1);
    }
}
