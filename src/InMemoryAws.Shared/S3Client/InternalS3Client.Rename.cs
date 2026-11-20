using System.Net;
using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>
/// RenameObject: moves an object of a directory bucket to another key in one step, under the source
/// and destination conditions it names, idempotently for a repeated <c>ClientToken</c>.
/// </summary>
internal sealed partial class InternalS3Client
{
    /// <summary>
    /// Renames <c>RenameSource</c> to <c>Key</c>: the destination takes the source's bytes and settings,
    /// and the source key goes. A <c>ClientToken</c> seen before with the same parameters answers
    /// success without renaming again; with other parameters it is <c>IdempotentParameterMismatch</c>.
    /// </summary>
    public override Task<RenameObjectOutput> RenameObjectAsync(RenameObjectRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            if (!bucket.IsDirectory)
            {
                throw S3Errors.NotImplemented("RenameObject");
            }

            var key = CheckKey(request.Key);
            var sourceKey = RenameSourceKey(bucket, request.RenameSource);
            var fingerprint = RenameFingerprint(key, sourceKey, request);
            if (request.ClientToken is { } token && bucket.RenameTokens.TryGetValue(token, out var seen))
            {
                return seen == fingerprint
                    ? Task.FromResult(new RenameObjectOutput())
                    : throw new S3Exception(
                        "IdempotentParameterMismatch",
                        "Parameters on this idempotent request are inconsistent with parameters used in previous request(s).",
                        HttpStatusCode.BadRequest);
            }

            var source = bucket.Current(sourceKey);
            if (source is null || source.IsDeleteMarker)
            {
                throw S3Errors.NoSuchKey(sourceKey);
            }

            CheckRenameConditions(source, request.SourceIfMatch, request.SourceIfNoneMatch, request.SourceIfModifiedSince, request.SourceIfUnmodifiedSince);
            var destination = bucket.Current(key);
            CheckRenameConditions(destination is { IsDeleteMarker: false } ? destination : null,
                request.DestinationIfMatch, request.DestinationIfNoneMatch, request.DestinationIfModifiedSince, request.DestinationIfUnmodifiedSince);

            var renamed = source.With(key, S3ObjectVersion.NullVersionId, source.LastModified);
            renamed.Annotations = source.Annotations;
            bucket.Remove(sourceKey, source.VersionId);
            OnObjectRemoved(bucket, source, "ObjectRemoved:Delete");
            if (destination is not null)
            {
                bucket.Remove(key, destination.VersionId);
            }

            bucket.Add(renamed);
            OnObjectWritten(bucket, renamed, "ObjectCreated:Put");
            if (request.ClientToken is { } newToken)
            {
                bucket.RenameTokens[newToken] = fingerprint;
            }

            return Task.FromResult(new RenameObjectOutput());
        }
    }

    /// <summary>The key <c>x-amz-rename-source</c> names: a key, or <c>/bucket/key</c> of this bucket, URL-encoded.</summary>
    private static string RenameSourceKey(S3Bucket bucket, string? renameSource)
    {
        if (string.IsNullOrEmpty(renameSource))
        {
            throw S3Errors.InvalidArgument("Rename Source must be specified", "x-amz-rename-source", renameSource);
        }

        var source = Http.S3Addressing.PercentDecode(renameSource!);
        var bucketPrefix = "/" + bucket.Name + "/";
        if (source.StartsWith(bucketPrefix, StringComparison.Ordinal))
        {
            source = source.Substring(bucketPrefix.Length);
        }

        return CheckKey(source);
    }

    /// <summary>Everything about a rename its client token stands for.</summary>
    private static string RenameFingerprint(string key, string sourceKey, RenameObjectRequest request) =>
        string.Join(
            "\n",
            key,
            sourceKey,
            request.DestinationIfMatch,
            request.DestinationIfNoneMatch,
            request.DestinationIfModifiedSince?.Ticks,
            request.DestinationIfUnmodifiedSince?.Ticks,
            request.SourceIfMatch,
            request.SourceIfNoneMatch,
            request.SourceIfModifiedSince?.Ticks,
            request.SourceIfUnmodifiedSince?.Ticks);

    /// <summary>
    /// One side's conditions: <c>If-Match</c> needs the object with a matching ETag, <c>If-None-Match</c>
    /// (<c>*</c> or an ETag) needs it absent or different, and the dates compare its last modification.
    /// Any that fails is <c>PreconditionFailed</c>.
    /// </summary>
    private static void CheckRenameConditions(S3ObjectVersion? version, string? ifMatch, string? ifNoneMatch, DateTime? ifModifiedSince, DateTime? ifUnmodifiedSince)
    {
        if (ifMatch is not null && (version is null || !ETagMatches(ifMatch, version.ETag)))
        {
            throw S3Errors.PreconditionFailed("If-Match");
        }

        if (ifNoneMatch is not null && version is not null && (ifNoneMatch == "*" || ETagMatches(ifNoneMatch, version.ETag)))
        {
            throw S3Errors.PreconditionFailed("If-None-Match");
        }

        if (ifModifiedSince is { } since && version is not null && version.LastModified <= since)
        {
            throw S3Errors.PreconditionFailed("If-Modified-Since");
        }

        if (ifUnmodifiedSince is { } until && version is not null && version.LastModified > until)
        {
            throw S3Errors.PreconditionFailed("If-Unmodified-Since");
        }
    }
}

/// <summary>What a bucket keeps for RenameObject.</summary>
internal sealed partial class S3Bucket
{
    /// <summary>The client tokens of renames done, with the parameters each was sent with.</summary>
    public Dictionary<string, string> RenameTokens { get; } = new(StringComparer.Ordinal);
}
