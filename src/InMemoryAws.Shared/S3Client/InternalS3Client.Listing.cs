using System.Globalization;
using System.Text;
using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

internal sealed partial class InternalS3Client
{
    private const int DefaultMaxKeys = 1000;

    public override Task<ListObjectsV2Output> ListObjectsV2Async(ListObjectsV2Request request, CancellationToken cancellationToken)
    {
        var maxKeys = CheckMaxKeys(request.MaxKeys);
        var encode = CheckEncodingType(request.EncodingType);
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            CheckDirectoryListing(bucket, request);
            string? after = request.StartAfter;
            if (request.ContinuationToken is not null)
            {
                after = DecodeToken(request.ContinuationToken);
            }

            var page = Walk(bucket, request.Prefix, request.Delimiter, after, maxKeys);
            var output = new ListObjectsV2Output
            {
                Name = bucket.Name,
                Prefix = Encode(request.Prefix ?? string.Empty, encode),
                Delimiter = request.Delimiter is null ? null : Encode(request.Delimiter, encode),
                MaxKeys = maxKeys,
                EncodingType = request.EncodingType,
                KeyCount = page.Entries.Count,
                IsTruncated = page.Truncated,
                ContinuationToken = request.ContinuationToken,
                NextContinuationToken = page.Truncated ? EncodeToken(page.Entries[^1].Name) : null,
                StartAfter = request.StartAfter is null ? null : Encode(request.StartAfter, encode),
                Contents = Contents(page, encode, request.FetchOwner == true),
                CommonPrefixes = CommonPrefixes(page, encode),
            };
            ListInflightUploads(bucket, request, output);
            return Task.FromResult(output);
        }
    }

    /// <summary>Refuses a listing a directory bucket can't serve.</summary>
    partial void CheckDirectoryListing(S3Bucket bucket, ListObjectsV2Request request);

    /// <summary>Adds what a directory bucket lists beyond its objects: the prefixes of uploads in progress.</summary>
    partial void ListInflightUploads(S3Bucket bucket, ListObjectsV2Request request, ListObjectsV2Output output);

    public override Task<ListObjectsOutput> ListObjectsAsync(ListObjectsRequest request, CancellationToken cancellationToken)
    {
        var maxKeys = CheckMaxKeys(request.MaxKeys);
        var encode = CheckEncodingType(request.EncodingType);
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var page = Walk(bucket, request.Prefix, request.Delimiter, request.Marker, maxKeys);
            return Task.FromResult(new ListObjectsOutput
            {
                Name = bucket.Name,
                Prefix = Encode(request.Prefix ?? string.Empty, encode),
                Marker = Encode(request.Marker ?? string.Empty, encode),
                Delimiter = request.Delimiter is null ? null : Encode(request.Delimiter, encode),
                MaxKeys = maxKeys,
                EncodingType = request.EncodingType,
                IsTruncated = page.Truncated,

                // S3 names the next marker only when the request has a delimiter; without one the
                // last key listed is the marker.
                NextMarker = page.Truncated && request.Delimiter is not null ? Encode(page.Entries[^1].Name, encode) : null,
                Contents = Contents(page, encode, owner: true),
                CommonPrefixes = CommonPrefixes(page, encode),
            });
        }
    }

    /// <summary>
    /// Every version and delete marker, by key and then newest first, from after the key and version
    /// markers, with keys under a delimiter rolled up into common prefixes.
    /// </summary>
    public override Task<ListObjectVersionsOutput> ListObjectVersionsAsync(ListObjectVersionsRequest request, CancellationToken cancellationToken)
    {
        var maxKeys = CheckMaxKeys(request.MaxKeys);
        var encode = CheckEncodingType(request.EncodingType);
        if (request.VersionIdMarker is not null && string.IsNullOrEmpty(request.KeyMarker))
        {
            throw S3Errors.InvalidArgument("A version-id marker cannot be specified without a key marker.", "version-id-marker", request.VersionIdMarker);
        }

        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var output = new ListObjectVersionsOutput
            {
                Name = bucket.Name,
                Prefix = Encode(request.Prefix ?? string.Empty, encode),
                KeyMarker = Encode(request.KeyMarker ?? string.Empty, encode),
                VersionIdMarker = request.VersionIdMarker ?? (request.KeyMarker is null ? null : string.Empty),
                Delimiter = request.Delimiter is null ? null : Encode(request.Delimiter, encode),
                MaxKeys = maxKeys,
                EncodingType = request.EncodingType,
                IsTruncated = false,
            };

            var count = 0;
            string? lastPrefix = null;
            string? lastKey = null;
            string? lastVersion = null;
            foreach (var entry in bucket.Objects.Values)
            {
                var key = entry.Key;
                if (request.Prefix is not null && !key.StartsWith(request.Prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (CommonPrefixOf(key, request.Prefix, request.Delimiter) is { } commonPrefix)
                {
                    if (commonPrefix == lastPrefix || (request.KeyMarker is not null && S3KeyComparer.Instance.Compare(commonPrefix, request.KeyMarker) <= 0))
                    {
                        continue;
                    }

                    if (count == maxKeys)
                    {
                        output.IsTruncated = true;
                        break;
                    }

                    (output.CommonPrefixes ??= []).Add(new CommonPrefix { Prefix = Encode(commonPrefix, encode) });
                    lastPrefix = commonPrefix;
                    lastKey = commonPrefix;
                    lastVersion = null;
                    count++;
                    continue;
                }

                var keyComparison = request.KeyMarker is null ? 1 : S3KeyComparer.Instance.Compare(key, request.KeyMarker);
                if (keyComparison < 0)
                {
                    continue;
                }

                var skipping = keyComparison == 0;
                if (skipping && string.IsNullOrEmpty(request.VersionIdMarker))
                {
                    continue;
                }

                var truncated = false;
                for (var i = 0; i < entry.Versions.Count; i++)
                {
                    var version = entry.Versions[i];
                    if (skipping)
                    {
                        if (version.VersionId == request.VersionIdMarker)
                        {
                            skipping = false;
                        }

                        continue;
                    }

                    if (count == maxKeys)
                    {
                        truncated = true;
                        break;
                    }

                    var owner = Owner(version.OwnerId);
                    if (version.IsDeleteMarker)
                    {
                        (output.DeleteMarkers ??= []).Add(new DeleteMarkerEntry
                        {
                            Key = Encode(key, encode),
                            VersionId = version.VersionId,
                            IsLatest = i == 0,
                            LastModified = version.LastModified,
                            Owner = owner,
                        });
                    }
                    else
                    {
                        (output.Versions ??= []).Add(new ObjectVersion
                        {
                            Key = Encode(key, encode),
                            VersionId = version.VersionId,
                            IsLatest = i == 0,
                            LastModified = version.LastModified,
                            ETag = version.ETag,
                            Size = version.Size,
                            StorageClass = version.StorageClass,
                            Owner = owner,
                            ChecksumAlgorithm = version.ChecksumAlgorithm is null ? null : [version.ChecksumAlgorithm],
                            ChecksumType = version.ChecksumType,
                        });
                    }

                    lastKey = key;
                    lastVersion = version.VersionId;
                    count++;
                }

                if (truncated)
                {
                    output.IsTruncated = true;
                    break;
                }
            }

            if (output.IsTruncated == true)
            {
                output.NextKeyMarker = lastKey is null ? null : Encode(lastKey, encode);
                output.NextVersionIdMarker = lastVersion;
            }

            return Task.FromResult(output);
        }
    }

    /// <summary>One page of a bucket's current objects and common prefixes.</summary>
    private sealed class ListingPage
    {
        public List<(string Name, S3ObjectVersion? Version)> Entries { get; } = [];

        public bool Truncated { get; set; }
    }

    /// <summary>
    /// The current objects under <paramref name="prefix"/> after <paramref name="after"/>, in key
    /// order, with keys that hold <paramref name="delimiter"/> past the prefix rolled up into one
    /// common prefix each; up to <paramref name="maxKeys"/> entries, objects and prefixes alike.
    /// </summary>
    private static ListingPage Walk(S3Bucket bucket, string? prefix, string? delimiter, string? after, int maxKeys)
    {
        var page = new ListingPage();
        string? lastPrefix = null;
        foreach (var entry in bucket.Objects.Values)
        {
            var key = entry.Key;
            var current = entry.Versions.Count > 0 ? entry.Versions[0] : null;
            if (current is null || current.IsDeleteMarker)
            {
                continue;
            }

            if (prefix is not null && !key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var name = CommonPrefixOf(key, prefix, delimiter);
            if (name is not null && name == lastPrefix)
            {
                continue;
            }

            var listed = name ?? key;
            if (after is not null && (S3KeyComparer.Instance.Compare(listed, after) <= 0 || (name is not null && after.StartsWith(name, StringComparison.Ordinal))))
            {
                continue;
            }

            if (page.Entries.Count == maxKeys)
            {
                page.Truncated = true;
                break;
            }

            page.Entries.Add((listed, name is null ? current : null));
            lastPrefix = name;
        }

        return page;
    }

    /// <summary>The common prefix a key rolls up into: the key up to the first delimiter after the prefix; null when it has none.</summary>
    private static string? CommonPrefixOf(string key, string? prefix, string? delimiter)
    {
        if (string.IsNullOrEmpty(delimiter))
        {
            return null;
        }

        var start = prefix?.Length ?? 0;
        var index = key.IndexOf(delimiter!, start, StringComparison.Ordinal);
        return index < 0 ? null : key.Substring(0, index + delimiter!.Length);
    }

    private List<S3Object>? Contents(ListingPage page, bool encode, bool owner)
    {
        List<S3Object>? contents = null;
        foreach (var (name, version) in page.Entries)
        {
            if (version is null)
            {
                continue;
            }

            (contents ??= []).Add(new S3Object
            {
                Key = Encode(name, encode),
                LastModified = version.LastModified,
                ETag = version.ETag,
                Size = version.Size,
                StorageClass = version.StorageClass,
                Owner = owner ? Owner(version.OwnerId) : null,
                ChecksumAlgorithm = version.ChecksumAlgorithm is null ? null : [version.ChecksumAlgorithm],
                ChecksumType = version.ChecksumType,
            });
        }

        return contents;
    }

    private static List<CommonPrefix>? CommonPrefixes(ListingPage page, bool encode)
    {
        List<CommonPrefix>? prefixes = null;
        foreach (var (name, version) in page.Entries)
        {
            if (version is null)
            {
                (prefixes ??= []).Add(new CommonPrefix { Prefix = Encode(name, encode) });
            }
        }

        return prefixes;
    }

    /// <summary><c>max-keys</c>: 1000 when absent, and at most 1000; a negative value is <c>InvalidArgument</c>.</summary>
    private static int CheckMaxKeys(int? maxKeys)
    {
        if (maxKeys is null)
        {
            return DefaultMaxKeys;
        }

        if (maxKeys < 0)
        {
            throw S3Errors.InvalidArgument("Argument maxKeys must be an integer between 0 and 2147483647", "maxKeys", maxKeys.Value.ToString(CultureInfo.InvariantCulture));
        }

        return Math.Min(maxKeys.Value, DefaultMaxKeys);
    }

    /// <summary><c>encoding-type</c>: only <c>url</c>.</summary>
    private static bool CheckEncodingType(string? encodingType) => encodingType switch
    {
        null => false,
        "url" => true,
        _ => throw S3Errors.InvalidArgument("Invalid Encoding Method specified in Request", "encoding-type", encodingType),
    };

    /// <summary>
    /// A key or prefix as <c>encoding-type=url</c> answers it: UTF-8, form-encoded (a space is
    /// <c>+</c>), percent-encoded except for unreserved characters and <c>/</c>.
    /// </summary>
    private static string Encode(string value, bool encode)
    {
        if (!encode)
        {
            return value;
        }

        var text = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '~' or '/')
            {
                text.Append(c);
            }
            else if (c == ' ')
            {
                text.Append('+');
            }
            else
            {
                text.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return text.ToString();
    }
}
