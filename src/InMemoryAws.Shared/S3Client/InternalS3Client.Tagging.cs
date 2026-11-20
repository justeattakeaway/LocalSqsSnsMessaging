using System.Globalization;
using System.Net;
using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

internal sealed partial class InternalS3Client
{
    public override Task<PutObjectTaggingOutput> PutObjectTaggingAsync(PutObjectTaggingRequest request, CancellationToken cancellationToken)
    {
        var tags = request.Tagging?.TagSet ?? throw S3Errors.MalformedXml();
        CheckObjectTags(tags);
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var version = TaggedVersion(bucket, request.Key, request.VersionId);
            version.Tags = [.. tags.Select(t => new Tag { Key = t.Key, Value = t.Value })];
            OnObjectTagged(bucket, version, "ObjectTagging:Put");
            return Task.FromResult(new PutObjectTaggingOutput { VersionId = VersionIdHeader(bucket, version) });
        }
    }

    public override Task<GetObjectTaggingOutput> GetObjectTaggingAsync(GetObjectTaggingRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var version = TaggedVersion(bucket, request.Key, request.VersionId);
            return Task.FromResult(new GetObjectTaggingOutput
            {
                VersionId = VersionIdHeader(bucket, version),
                TagSet = [.. version.Tags.Select(t => new Tag { Key = t.Key, Value = t.Value })],
            });
        }
    }

    public override Task<DeleteObjectTaggingOutput> DeleteObjectTaggingAsync(DeleteObjectTaggingRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var version = TaggedVersion(bucket, request.Key, request.VersionId);
            version.Tags = [];
            OnObjectTagged(bucket, version, "ObjectTagging:Delete");
            return Task.FromResult(new DeleteObjectTaggingOutput { VersionId = VersionIdHeader(bucket, version) });
        }
    }

    /// <summary>Called under the store's lock after an object's tags change (notifications hook in here).</summary>
    partial void OnObjectTagged(S3Bucket bucket, S3ObjectVersion version, string eventName);

    public override Task<PutBucketTaggingOutput> PutBucketTaggingAsync(PutBucketTaggingRequest request, CancellationToken cancellationToken)
    {
        var tags = request.Tagging?.TagSet ?? throw S3Errors.MalformedXml();
        CheckTags(tags, 50, "Bucket tag count cannot be greater than 50");
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            bucket.Tags = [.. tags.Select(t => new Tag { Key = t.Key, Value = t.Value })];
            return Task.FromResult(new PutBucketTaggingOutput());
        }
    }

    public override Task<GetBucketTaggingOutput> GetBucketTaggingAsync(GetBucketTaggingRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            if (bucket.Tags is not { Count: > 0 } tags)
            {
                throw new S3Exception("NoSuchTagSet", "The TagSet does not exist", HttpStatusCode.NotFound, ("BucketName", bucket.Name));
            }

            return Task.FromResult(new GetBucketTaggingOutput { TagSet = [.. tags.Select(t => new Tag { Key = t.Key, Value = t.Value })] });
        }
    }

    public override Task<DeleteBucketTaggingOutput> DeleteBucketTaggingAsync(DeleteBucketTaggingRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            bucket.Tags = null;
            return Task.FromResult(new DeleteBucketTaggingOutput());
        }
    }

    /// <summary>The version whose tags a request reads or writes: the current one, or the one named.</summary>
    private static S3ObjectVersion TaggedVersion(S3Bucket bucket, string? key, string? versionId)
    {
        var name = CheckKey(key);
        if (versionId is not null)
        {
            CheckVersionIdFormat(versionId);
            var version = bucket.Version(name, versionId) ?? throw S3Errors.NoSuchVersion(name, versionId);
            if (version.IsDeleteMarker)
            {
                throw S3Errors.MethodNotAllowed("GET", "DeleteMarker");
            }

            return version;
        }

        var current = bucket.Current(name);
        if (current is null || current.IsDeleteMarker)
        {
            throw S3Errors.NoSuchKey(name);
        }

        return current;
    }

    /// <summary>
    /// Whether <paramref name="text"/> holds only what a tag may: letters, numbers and white space of
    /// the Basic Multilingual Plane, and <c>+ - = . _ : / @</c>. A character outside the plane (an
    /// emoji) or a combining mark is refused; S3 checks the text as sent, without normalizing it.
    /// </summary>
    private static bool IsTagText(string text)
    {
        foreach (var c in text)
        {
            var allowed = c is '+' or '-' or '=' or '.' or '_' or ':' or '/' or '@'
                || char.IsWhiteSpace(c)
                || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                    or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
                    or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>An object's tags: at most 10, each checked as every tag is.</summary>
    private static void CheckObjectTags(List<Tag> tags) =>
        CheckTags(tags, 10, "Object tags cannot be greater than 10");

    /// <summary>
    /// Tags: at most <paramref name="limit"/>, unique keys of 1 to 128 characters and values of up to
    /// 256, none in the reserved <c>aws:</c> namespace.
    /// </summary>
    private static void CheckTags(List<Tag> tags, int limit, string tooMany)
    {
        if (tags.Count > limit)
        {
            throw new S3Exception("BadRequest", tooMany, HttpStatusCode.BadRequest);
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            var key = tag.Key ?? throw S3Errors.MalformedXml();
            var value = tag.Value ?? string.Empty;
            if (key.Length is 0 or > 128 || !IsTagText(key))
            {
                throw new S3Exception("InvalidTag", "The TagKey you have provided is invalid", HttpStatusCode.BadRequest, ("TagKey", key));
            }

            if (value.Length > 256 || !IsTagText(value))
            {
                throw new S3Exception("InvalidTag", "The TagValue you have provided is invalid", HttpStatusCode.BadRequest, ("TagKey", key), ("TagValue", value));
            }

            if (key.StartsWith("aws:", StringComparison.OrdinalIgnoreCase))
            {
                throw new S3Exception("InvalidTag", "Your TagKey cannot be prefixed with aws:", HttpStatusCode.BadRequest, ("TagKey", key));
            }

            if (!keys.Add(key))
            {
                throw new S3Exception("InvalidTag", "Cannot provide multiple Tags with the same key", HttpStatusCode.BadRequest, ("TagKey", key));
            }
        }
    }
}
