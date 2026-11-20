using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>
/// What the S3 Express One Zone ports share: the checks' tables of S3 Express Availability Zone ids,
/// <c>ec2:DescribeAvailabilityZones</c>, creating a directory bucket (with its teardown), the
/// <c>_err</c> helper that also reports the HTTP status, and session-pinned clients.
/// </summary>
public static partial class S3Fidelity
{
    /// <summary>The checks' <c>_S3EXPRESS_AZ_BY_REGION</c>: the zone ids of each region that offer S3 Express One Zone.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> ExpressZoneIds = new Dictionary<string, string[]>(StringComparer.Ordinal)
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

    /// <summary>The checks' <c>EXPRESS_ZONE_IDS</c>: one S3 Express zone id per region.</summary>
    public static readonly IReadOnlyDictionary<string, string> ExpressZoneId = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["us-east-1"] = "use1-az4",
        ["us-east-2"] = "use2-az1",
        ["us-west-2"] = "usw2-az1",
        ["ap-south-1"] = "aps1-az1",
        ["ap-northeast-1"] = "apne1-az1",
        ["eu-central-1"] = "euc1-az1",
        ["eu-west-1"] = "euw1-az1",
        ["eu-north-1"] = "eun1-az1",
    };

    /// <summary>The Availability Zone ids <c>ec2:DescribeAvailabilityZones</c> reports for each region, in its order.</summary>
    private static readonly Dictionary<string, string[]> RegionZoneIds = new(StringComparer.Ordinal)
    {
        ["us-east-1"] = ["use1-az1", "use1-az2", "use1-az3", "use1-az4", "use1-az5", "use1-az6"],
        ["us-east-2"] = ["use2-az1", "use2-az2", "use2-az3"],
        ["us-west-2"] = ["usw2-az1", "usw2-az2", "usw2-az3", "usw2-az4"],
        ["eu-west-1"] = ["euw1-az1", "euw1-az2", "euw1-az3"],
    };

    /// <summary>
    /// <c>[z["ZoneId"] for z in ec2.describe_availability_zones(...)["AvailabilityZones"]]</c>: the cloud
    /// models no EC2, so these are the region's zone ids, every one available.
    /// </summary>
    public static IReadOnlyList<string> AvailabilityZoneIds(FidelityContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return RegionZoneIds.TryGetValue(ctx.Region, out var zones)
            ? zones
            : throw Fail($"no availability zones known for region {Py.Repr(ctx.Region)}");
    }

    /// <summary>
    /// <c>s3.create_bucket(Bucket=name, CreateBucketConfiguration={"Location": {"Type": "AvailabilityZone",
    /// "Name": zone}, "Bucket": {"DataRedundancy": "SingleAvailabilityZone", "Type": "Directory"}})</c>.
    /// </summary>
    public static Task<PutBucketResponse> CreateDirectoryBucketRaw(IAmazonS3 s3, string name, string zone)
    {
        ArgumentNullException.ThrowIfNull(s3);
        return s3.PutBucketAsync(new PutBucketRequest
        {
            BucketName = name,
            UseClientRegion = false,
            PutBucketConfiguration = new PutBucketConfiguration
            {
                Location = new LocationInfo { Type = LocationType.AvailabilityZone, Name = zone },
                BucketInfo = new BucketInfo { DataRedundancy = DataRedundancy.SingleAvailabilityZone, Type = BucketType.Directory },
            },
        });
    }

    /// <summary>
    /// The checks' <c>_create_directory_bucket</c> / <c>_directory_bucket</c>: tries <c>{base}--{zone}--x-s3</c>
    /// in each zone in turn and keeps the first one S3 accepts, registering its teardown
    /// (<see cref="EmptyAndDeleteDirectoryBucket"/>) unless <paramref name="cleanup"/> is false.
    /// </summary>
    public static async Task<(string Bucket, string Zone)> CreateDirectoryBucket(
        FidelityContext ctx, IAmazonS3 s3, string baseName, IEnumerable<string> zones, bool cleanup = true)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(zones);
        AwsError? last = null;
        foreach (var zone in zones)
        {
            var name = $"{baseName}--{zone}--x-s3";
            last = await FidelityContext.CaptureError(() => CreateDirectoryBucketRaw(s3, name, zone));
            if (last is null)
            {
                if (cleanup)
                {
                    ctx.OnCleanup(() => EmptyAndDeleteDirectoryBucket(s3, name));
                }

                return (name, zone);
            }
        }

        throw Fail($"could not create an S3 Express directory bucket in {ctx.Region}: {last}");
    }

    /// <summary>
    /// The checks' <c>_new_directory_bucket</c>: <c>{unique_name(prefix)}--{EXPRESS_ZONE_IDS[region]}--x-s3</c>.
    /// </summary>
    public static async Task<string> NewDirectoryBucket(FidelityContext ctx, IAmazonS3 s3, string prefix = "sim2real-dir-")
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!ExpressZoneId.TryGetValue(ctx.Region, out var zone))
        {
            throw Fail($"no known S3 Express One Zone Availability Zone for region {Py.Repr(ctx.Region)}");
        }

        return (await CreateDirectoryBucket(ctx, s3, FidelityContext.UniqueName(prefix), [zone])).Bucket;
    }

    /// <summary>
    /// The directory bucket teardown: aborts every multipart upload, deletes every object, then the
    /// bucket, ignoring a bucket already gone.
    /// </summary>
    public static async Task EmptyAndDeleteDirectoryBucket(IAmazonS3 s3, string bucket)
    {
        ArgumentNullException.ThrowIfNull(s3);
        try
        {
            var uploads = await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket });
            foreach (var upload in uploads.MultipartUploads ?? [])
            {
                await FidelityContext.CaptureError(() => s3.AbortMultipartUploadAsync(bucket, upload.Key, upload.UploadId));
            }

            string? token = null;
            do
            {
                var page = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, ContinuationToken = token });
                foreach (var item in page.S3Objects ?? [])
                {
                    await s3.DeleteObjectAsync(bucket, item.Key);
                }

                token = page.IsTruncated == true ? page.NextContinuationToken : null;
            }
            while (token is not null);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "NoSuchBucket" or "NoSuchKey")
        {
        }

        try
        {
            await s3.DeleteBucketAsync(bucket);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchBucket")
        {
        }
    }

    /// <summary>
    /// The checks' <c>_err(fn)</c>: <c>(Error.Code, HTTP status, Error.Message)</c> of the error the call
    /// raises; a success fails the check.
    /// </summary>
    public static async Task<(string? Code, int Status, string? Message)> ErrorWithStatus(Func<Task> call, string? whenSucceeded = null)
    {
        var error = await Error(call, whenSucceeded ?? "expected a ClientError, but the call SUCCEEDED");
        var (code, message) = Botocore(error);
        return (code, (int)error.StatusCode, message);
    }

    /// <summary>
    /// The checks' <c>_session_client(ctx, session)</c>: an S3 client that signs every directory bucket
    /// request with exactly the session CreateSession returned, and never mints another. Its stats list
    /// every operation the client sent, and count the requests signed with the pinned session.
    /// </summary>
    public static (AmazonS3Client Client, SessionStats Stats) SessionClient(FidelityContext ctx, CreateSessionResponse session)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(session);
        var stats = new SessionStats();
        var pinned = new PinnedSession(session.Credentials, stats);
        var c = session.Credentials;
        var client = ctx.S3SessionClient(pinned, new SessionAWSCredentials(c.AccessKeyId, c.SecretAccessKey, c.SessionToken));
        client.BeforeRequestEvent += (_, e) =>
        {
            if (e is WebServiceRequestEventArgs args)
            {
                var name = args.Request.GetType().Name;
                stats.Ops.Add(name.EndsWith("Request", StringComparison.Ordinal) ? name[..^"Request".Length] : name);
            }
        };
        return (client, stats);
    }

    /// <summary><c>s3.create_session(Bucket=bucket, SessionMode=mode)</c>.</summary>
    public static Task<CreateSessionResponse> CreateSession(IAmazonS3 s3, string bucket, string? mode = null)
    {
        ArgumentNullException.ThrowIfNull(s3);
        var request = new CreateSessionRequest { BucketName = bucket };
        if (mode is not null)
        {
            request.SessionMode = new SessionMode(mode);
        }

        return s3.CreateSessionAsync(request);
    }

    /// <summary>The checks' <c>_general_purpose_bucket(ctx, s3, prefix)</c>: a bucket tagged <c>sim2real</c>=the check.</summary>
    public static async Task<string> GeneralPurposeBucket(FidelityContext ctx, IAmazonS3 s3, string prefix, string check)
    {
        ArgumentNullException.ThrowIfNull(s3);
        var name = await CreateBucket(ctx, prefix, s3 as AmazonS3Client);
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = name, TagSet = [new Tag { Key = "sim2real", Value = check }] });
        return name;
    }

    /// <summary>What a session-pinned client did: the operations it sent, and the requests signed with the pinned session.</summary>
    public sealed class SessionStats
    {
        public List<string> Ops { get; } = [];

        public int Pinned { get; set; }
    }

    /// <summary>
    /// Stands in for the SDK's S3 Express identity cache and always returns the one session it was given:
    /// the SDK's own cache calls CreateSession whenever it holds no live session, which would silently
    /// swap in a fresh token; this one never calls anything.
    /// </summary>
    private sealed class PinnedSession(SessionCredentials credentials, SessionStats stats) : IS3ExpressCredentialProvider
    {
        public SessionCredentials ResolveSessionCredentials(string bucketName)
        {
            stats.Pinned++;
            return credentials;
        }

        public Task<SessionCredentials> ResolveSessionCredentialsAsync(string bucketName, CancellationToken cancellationToken = default) =>
            Task.FromResult(ResolveSessionCredentials(bucketName));

        public void Dispose()
        {
        }
    }
}

/// <summary>The s3x checks' shared steps.</summary>
public static partial class S3Fidelity
{
    /// <summary>The s3x checks' <c>_directory_bucket(ctx, s3, prefix)</c>: a directory bucket in the region's first S3 Express zone that takes one.</summary>
    public static async Task<string> DirectoryBucket(FidelityContext ctx, IAmazonS3 s3, string prefix)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!ExpressZoneIds.TryGetValue(ctx.Region, out var zones))
        {
            throw Fail($"S3 Express One Zone AZ IDs not known for region {Py.Repr(ctx.Region)}");
        }

        return (await CreateDirectoryBucket(ctx, s3, FidelityContext.UniqueName(prefix), zones)).Bucket;
    }

    /// <summary><c>sorted(o["Key"] for o in resp.get("Contents", []))</c>.</summary>
    public static List<string> SortedKeys(ListObjectsV2Response response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return [.. (response.S3Objects ?? []).Select(o => o.Key).Order(StringComparer.Ordinal)];
    }

    /// <summary><c>sorted(p["Prefix"] for p in resp.get("CommonPrefixes", []))</c>.</summary>
    public static List<string> SortedPrefixes(ListObjectsV2Response response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return [.. (response.CommonPrefixes ?? []).Order(StringComparer.Ordinal)];
    }

    /// <summary><c>repr()</c> of a (code, status, message) triple.</summary>
    public static string Repr((string? Code, int Status, string? Message) error) =>
        $"({Py.Repr(error.Code)}, {error.Status}, {Py.Repr(error.Message)})";
}

/// <summary>Appends to directory bucket objects.</summary>
public static partial class S3Fidelity
{
    /// <summary><c>s3.put_object(Bucket=bucket, Key=key, Body=body, WriteOffsetBytes=offset)</c>.</summary>
    public static Task<PutObjectResponse> Append(IAmazonS3 s3, string bucket, string key, byte[] body, long offset)
    {
        ArgumentNullException.ThrowIfNull(s3);
        return s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body), WriteOffsetBytes = offset });
    }
}
