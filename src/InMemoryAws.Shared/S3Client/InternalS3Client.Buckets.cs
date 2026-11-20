using System.Net;
using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

internal sealed partial class InternalS3Client
{
    /// <summary>
    /// Creates a bucket in the request's region. Outside <c>us-east-1</c> the request must name that
    /// region as its <c>LocationConstraint</c>; <c>us-east-1</c> takes no constraint, or any other
    /// region's. Bucket names are global: a name another account holds is <c>BucketAlreadyExists</c>,
    /// and one this account holds is <c>BucketAlreadyOwnedByYou</c>, except in <c>us-east-1</c>,
    /// which answers success.
    /// </summary>
    public override Task<CreateBucketOutput> CreateBucketAsync(CreateBucketRequest request, CancellationToken cancellationToken)
    {
        var name = request.Bucket ?? string.Empty;
        if (request.CreateBucketConfiguration?.Bucket is not null)
        {
            return CreateDirectoryBucket(name, request);
        }

        CheckReservedBucketName(name, request);
        if (!S3BucketNames.IsValid(name))
        {
            throw S3Errors.InvalidBucketName(name);
        }

        var constraint = request.CreateBucketConfiguration?.LocationConstraint;
        string bucketRegion;
        if (Region == "us-east-1")
        {
            if (constraint == "us-east-1")
            {
                throw S3Errors.InvalidLocationConstraint(constraint);
            }

            bucketRegion = string.IsNullOrEmpty(constraint) ? "us-east-1" : constraint == "EU" ? "eu-west-1" : constraint!;
        }
        else if (string.IsNullOrEmpty(constraint))
        {
            throw S3Errors.IllegalLocationConstraint(null);
        }
        else if (!string.Equals(constraint, Region, StringComparison.Ordinal))
        {
            throw S3Errors.IllegalLocationConstraint(constraint);
        }
        else
        {
            bucketRegion = Region;
        }

        lock (Store.Gate)
        {
            if (Store.Buckets.TryGetValue(name, out var existing))
            {
                if (existing.OwnerId != OwnerId)
                {
                    throw S3Errors.BucketAlreadyExists(name);
                }

                if (Region != "us-east-1" || existing.Region != "us-east-1")
                {
                    throw S3Errors.BucketAlreadyOwnedByYou(name);
                }

                return Task.FromResult(new CreateBucketOutput { Location = "/" + name, BucketArn = existing.Arn });
            }

            var bucket = new S3Bucket(name, bucketRegion, OwnerId, NowToTheSecond);
            if (request.ObjectLockEnabledForBucket == true)
            {
                bucket.ObjectLockEnabled = true;
                bucket.Versioning = "Enabled";
            }

            if (request.CreateBucketConfiguration?.Tags is { Count: > 0 } tags)
            {
                bucket.Tags = [.. tags];
            }

            OnBucketCreated(bucket, request);
            Store.Buckets.Add(name, bucket);
            return Task.FromResult(new CreateBucketOutput
            {
                Location = bucketRegion == "us-east-1" ? "/" + name : $"http://{name}.s3.amazonaws.com/",
                BucketArn = bucket.Arn,
            });
        }
    }

    /// <summary>Applies the rest of a CreateBucket request (ACL, object ownership, ...) to the new bucket.</summary>
    partial void OnBucketCreated(S3Bucket bucket, CreateBucketRequest request);

    /// <summary>Deletes an empty bucket: one with no objects, versions or delete markers.</summary>
    public override Task<DeleteBucketOutput> DeleteBucketAsync(DeleteBucketRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            if (!bucket.IsEmpty)
            {
                throw S3Errors.BucketNotEmpty(bucket.Name, versioned: bucket.Versioning is not null);
            }

            Store.Buckets.Remove(bucket.Name);
            return Task.FromResult(new DeleteBucketOutput());
        }
    }

    public override Task<HeadBucketOutput> HeadBucketAsync(HeadBucketRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            return Task.FromResult(new HeadBucketOutput
            {
                BucketRegion = bucket.Region,
                AccessPointAlias = false,
                BucketArn = bucket.Arn,
            });
        }
    }

    /// <summary>
    /// The account's buckets in name order, in every region (or the one <c>BucketRegion</c> names),
    /// paged by <c>MaxBuckets</c> with a continuation token.
    /// </summary>
    public override Task<ListBucketsOutput> ListBucketsAsync(ListBucketsRequest request, CancellationToken cancellationToken)
    {
        if (request.MaxBuckets is < 1 or > 10000)
        {
            throw S3Errors.InvalidArgument("Argument max-buckets must be an integer between 1 and 10000", "max-buckets", request.MaxBuckets.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        lock (Store.Gate)
        {
            var after = DecodeToken(request.ContinuationToken);
            var buckets = Store.Buckets.Values
                .Where(b => b.OwnerId == OwnerId && !b.IsDirectory)
                .Where(b => request.Prefix is null || b.Name.StartsWith(request.Prefix, StringComparison.Ordinal))
                .Where(b => request.BucketRegion is null || b.Region == request.BucketRegion)
                .OrderBy(b => b.Name, StringComparer.Ordinal)
                .Where(b => after is null || string.CompareOrdinal(b.Name, after) > 0)
                .ToList();
            var page = request.MaxBuckets is { } max ? buckets.Take(max).ToList() : buckets;
            return Task.FromResult(new ListBucketsOutput
            {
                Buckets = [.. page.Select(b => new Bucket { Name = b.Name, CreationDate = b.Created, BucketRegion = b.Region, BucketArn = b.Arn })],
                Owner = Owner(),
                Prefix = request.Prefix,
                ContinuationToken = page.Count < buckets.Count ? EncodeToken(page[^1].Name) : null,
            });
        }
    }

    /// <summary>The bucket's region: empty for <c>us-east-1</c>, as S3 answers it.</summary>
    public override Task<GetBucketLocationOutput> GetBucketLocationAsync(GetBucketLocationRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            if (string.IsNullOrEmpty(request.Bucket) || !Store.Buckets.TryGetValue(request.Bucket!, out var bucket))
            {
                throw S3Errors.NoSuchBucket(request.Bucket);
            }

            // GetBucketLocation is answered from any region.
            return Task.FromResult(new GetBucketLocationOutput { LocationConstraint = bucket.Region == "us-east-1" ? null : bucket.Region });
        }
    }

    /// <summary>Turns versioning on or suspends it; it can never be turned off again.</summary>
    public override Task<PutBucketVersioningOutput> PutBucketVersioningAsync(PutBucketVersioningRequest request, CancellationToken cancellationToken)
    {
        var configuration = request.VersioningConfiguration ?? throw S3Errors.MalformedXml();
        if (configuration.Status is not (null or "Enabled" or "Suspended") || configuration.MFADelete is not (null or "Enabled" or "Disabled"))
        {
            throw S3Errors.MalformedXml();
        }

        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            if (configuration.Status == "Suspended" && bucket.ObjectLockEnabled)
            {
                throw new S3Exception("InvalidBucketState", "An Object Lock configuration is present on this bucket, so the versioning state cannot be changed.", HttpStatusCode.Conflict);
            }

            if (configuration.Status is not null)
            {
                bucket.Versioning = configuration.Status;
            }

            if (configuration.MFADelete is not null)
            {
                bucket.MfaDelete = configuration.MFADelete;
            }

            return Task.FromResult(new PutBucketVersioningOutput());
        }
    }

    public override Task<GetBucketVersioningOutput> GetBucketVersioningAsync(GetBucketVersioningRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            return Task.FromResult(new GetBucketVersioningOutput { Status = bucket.Versioning, MFADelete = bucket.MfaDelete });
        }
    }

    private static string EncodeToken(string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));

    private static string? DecodeToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        try
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token!));
        }
        catch (FormatException)
        {
            throw S3Errors.InvalidArgument("The continuation token provided is incorrect", "continuation-token", token);
        }
    }
}

/// <summary>S3's rules for general purpose bucket names.</summary>
internal static class S3BucketNames
{
    private static readonly string[] ReservedPrefixes = ["xn--", "sthree-", "amzn-s3-demo-"];
    private static readonly string[] ReservedSuffixes = ["-s3alias", "--ol-s3", ".mrap", "--x-s3", "--table-s3"];

    /// <summary>
    /// 3 to 63 lower-case letters, digits, dots and hyphens, starting and ending with a letter or
    /// digit, with no two dots together, not shaped like an IP address, and without a reserved prefix
    /// or suffix.
    /// </summary>
    public static bool IsValid(string name)
    {
        if (name.Length is < 3 or > 63)
        {
            return false;
        }

        foreach (var c in name)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-'))
            {
                return false;
            }
        }

        if (!char.IsLetterOrDigit(name[0]) || !char.IsLetterOrDigit(name[^1]) || name.Contains("..", StringComparison.Ordinal)
            || name.Contains(".-", StringComparison.Ordinal) || name.Contains("-.", StringComparison.Ordinal))
        {
            return false;
        }

        if (System.Net.IPAddress.TryParse(name, out _) && name.Count(c => c == '.') == 3)
        {
            return false;
        }

        return !ReservedPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))
            && !ReservedSuffixes.Any(s => name.EndsWith(s, StringComparison.Ordinal));
    }
}
