#pragma warning disable CA1822 // The partial method is declared on the instance.
using System.Net;
using System.Text;
using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>
/// Object annotations: named payloads kept on an object version (Put, Get, Delete and
/// ListObjectAnnotations), copied with the object unless a copy says <c>EXCLUDE</c>.
/// </summary>
internal sealed partial class InternalS3Client
{
    /// <summary>The most annotations one object version holds.</summary>
    private const int MaxAnnotations = 1000;

    /// <summary>The longest annotation name, in UTF-8 bytes.</summary>
    private const int MaxAnnotationNameBytes = 512;

    /// <summary>
    /// Adds an annotation to an object version, or replaces the one of the same name. A version holds
    /// at most 1,000 (<c>AnnotationLimitExceeded</c>).
    /// </summary>
    public override Task<PutObjectAnnotationOutput> PutObjectAnnotationAsync(PutObjectAnnotationRequest request, CancellationToken cancellationToken)
    {
        var name = CheckAnnotationName(request.AnnotationName);
        var payload = request.AnnotationPayload?.ToArray() ?? [];
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var version = AnnotatedVersion(bucket, request.Key, request.VersionId);
            var annotations = version.Annotations ??= new SortedDictionary<string, S3Annotation>(S3KeyComparer.Instance);
            if (!annotations.ContainsKey(name) && annotations.Count >= MaxAnnotations)
            {
                throw new S3Exception(
                    "AnnotationLimitExceeded",
                    $"The object annotation count ({annotations.Count + 1}) exceeds the limit ({MaxAnnotations}).",
                    HttpStatusCode.BadRequest);
            }

            var annotation = new S3Annotation(payload, NowToTheSecond, S3Checksums.ETag(payload));
            annotations[name] = annotation;
            return Task.FromResult(new PutObjectAnnotationOutput
            {
                Key = version.Key,
                AnnotationName = name,
                ObjectVersionId = VersionIdHeader(bucket, version),
                ETag = annotation.ETag,
            });
        }
    }

    /// <summary>An annotation's payload; <c>NoSuchAnnotation</c> when the version has none of that name.</summary>
    public override Task<GetObjectAnnotationOutput> GetObjectAnnotationAsync(GetObjectAnnotationRequest request, CancellationToken cancellationToken)
    {
        var name = CheckAnnotationName(request.AnnotationName);
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var version = AnnotatedVersion(bucket, request.Key, request.VersionId);
            if (version.Annotations is null || !version.Annotations.TryGetValue(name, out var annotation))
            {
                throw new S3Exception("NoSuchAnnotation", "The specified annotation does not exist.", HttpStatusCode.NotFound);
            }

            return Task.FromResult(new GetObjectAnnotationOutput
            {
                AnnotationPayload = annotation.Payload,
                ContentLength = annotation.Payload.LongLength,
                ETag = annotation.ETag,
                LastModified = annotation.LastModified,
                ObjectVersionId = VersionIdHeader(bucket, version),
            });
        }
    }

    /// <summary>Removes an annotation; removing one the version doesn't have succeeds.</summary>
    public override Task<DeleteObjectAnnotationOutput> DeleteObjectAnnotationAsync(DeleteObjectAnnotationRequest request, CancellationToken cancellationToken)
    {
        var name = CheckAnnotationName(request.AnnotationName);
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var version = AnnotatedVersion(bucket, request.Key, request.VersionId);
            version.Annotations?.Remove(name);
            return Task.FromResult(new DeleteObjectAnnotationOutput());
        }
    }

    /// <summary>
    /// A version's annotations in name order, under an optional prefix, paged by
    /// <c>MaxAnnotationResults</c> (1,000 by default); <c>AnnotationCount</c> counts the page.
    /// </summary>
    public override Task<ListObjectAnnotationsOutput> ListObjectAnnotationsAsync(ListObjectAnnotationsRequest request, CancellationToken cancellationToken)
    {
        if (request.AnnotationPrefix is { } annotationPrefix && annotationPrefix.Any(c => !IsAnnotationNameCharacter(c)))
        {
            throw new S3Exception("InvalidPrefix", "The annotation prefix you have provided is invalid.", HttpStatusCode.BadRequest);
        }

        if (request.MaxAnnotationResults is < 1 or > MaxAnnotations)
        {
            throw S3Errors.InvalidArgument("Argument max-annotation-results must be an integer between 1 and 1000", "max-annotation-results",
                request.MaxAnnotationResults.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var max = request.MaxAnnotationResults ?? MaxAnnotations;
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var version = AnnotatedVersion(bucket, request.Key, request.VersionId);
            var after = DecodeToken(request.ContinuationToken);
            var matching = (version.Annotations ?? Enumerable.Empty<KeyValuePair<string, S3Annotation>>())
                .Where(a => request.AnnotationPrefix is null || a.Key.StartsWith(request.AnnotationPrefix, StringComparison.Ordinal))
                .Where(a => after is null || S3KeyComparer.Instance.Compare(a.Key, after) > 0)
                .ToList();
            var page = matching.Take(max).ToList();
            return Task.FromResult(new ListObjectAnnotationsOutput
            {
                Bucket = bucket.Name,
                Key = version.Key,
                ObjectVersionId = VersionIdHeader(bucket, version),
                AnnotationPrefix = request.AnnotationPrefix,
                MaxAnnotationResults = max,
                AnnotationCount = page.Count,
                ContinuationToken = request.ContinuationToken,
                NextContinuationToken = page.Count < matching.Count ? EncodeToken(page[^1].Key) : null,
                Annotations = [.. page.Select(a => new AnnotationEntry
                {
                    AnnotationName = a.Key,
                    LastModified = a.Value.LastModified,
                    ETag = a.Value.ETag,
                    Size = a.Value.Payload.LongLength,
                })],
            });
        }
    }

    /// <summary>
    /// The object version an annotation request names: the current version, or the one its version id
    /// names. A key with no object (or a delete marker) is <c>NoSuchKey</c>.
    /// </summary>
    private static S3ObjectVersion AnnotatedVersion(S3Bucket bucket, string? key, string? versionId)
    {
        var checkedKey = CheckKey(key);
        if (versionId is not null)
        {
            CheckVersionIdFormat(versionId);
            var version = bucket.Version(checkedKey, versionId) ?? throw S3Errors.NoSuchVersion(checkedKey, versionId);
            return version.IsDeleteMarker ? throw S3Errors.NoSuchKey(checkedKey) : version;
        }

        var current = bucket.Current(checkedKey);
        return current is null || current.IsDeleteMarker ? throw S3Errors.NoSuchKey(checkedKey) : current;
    }

    /// <summary>
    /// An annotation name: at most 512 bytes (<c>AnnotationNameTooLong</c>), not starting with
    /// <c>aws</c> or <c>s3</c>, and made of letters, digits, <c>.</c>, <c>-</c> and <c>_</c>
    /// (<c>InvalidAnnotation</c>).
    /// </summary>
    private static string CheckAnnotationName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new S3Exception("InvalidAnnotation", "Your annotation name contains invalid characters.", HttpStatusCode.BadRequest);
        }

        if (Encoding.UTF8.GetByteCount(name) > MaxAnnotationNameBytes)
        {
            throw new S3Exception("AnnotationNameTooLong", "Your annotation name is too long.", HttpStatusCode.BadRequest);
        }

        if (name!.StartsWith("aws", StringComparison.OrdinalIgnoreCase) || name.StartsWith("s3", StringComparison.OrdinalIgnoreCase))
        {
            throw new S3Exception("InvalidAnnotation", "Your annotation name cannot start with 'aws' or 's3'.", HttpStatusCode.BadRequest);
        }

        if (name.Any(c => !IsAnnotationNameCharacter(c)))
        {
            throw new S3Exception("InvalidAnnotation", "Your annotation name contains invalid characters.", HttpStatusCode.BadRequest);
        }

        return name;
    }

    private static bool IsAnnotationNameCharacter(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-' or '_';

    /// <summary>
    /// Before a copy is stored: a directory bucket refuses what it can't take, and the copy takes the
    /// source's annotations unless its <c>AnnotationDirective</c> is <c>EXCLUDE</c>.
    /// </summary>
    partial void OnCopying(S3Bucket bucket, S3ObjectVersion source, S3ObjectVersion version, CopyObjectRequest request)
    {
        CheckDirectoryCopy(bucket, source, request);
        if (request.AnnotationDirective is not (null or "COPY" or "EXCLUDE"))
        {
            throw S3Errors.InvalidArgument("Unknown annotation directive.", "x-amz-object-annotation-directive", request.AnnotationDirective);
        }

        if (request.AnnotationDirective != "EXCLUDE" && source.Annotations is { Count: > 0 } annotations)
        {
            version.Annotations = new SortedDictionary<string, S3Annotation>(annotations, S3KeyComparer.Instance);
        }
    }
}

/// <summary>One annotation on an object version: its payload, when it was written, and its ETag.</summary>
internal sealed record S3Annotation(byte[] Payload, DateTime LastModified, string ETag);

/// <summary>What an object version keeps for annotations.</summary>
internal sealed partial class S3ObjectVersion
{
    /// <summary>The version's annotations by name, in name order; null until one is put.</summary>
    public SortedDictionary<string, S3Annotation>? Annotations { get; set; }
}
