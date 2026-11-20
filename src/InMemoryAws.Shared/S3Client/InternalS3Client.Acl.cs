using System.Net;
using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>
/// A bucket's Object Ownership setting, its access control list (ACL), and its server access logging
/// configuration.
/// </summary>
internal sealed partial class S3Bucket
{
    /// <summary>
    /// The bucket's Object Ownership: <c>BucketOwnerEnforced</c> (ACLs disabled, S3's default since
    /// April 2023), <c>BucketOwnerPreferred</c> or <c>ObjectWriter</c>; null once
    /// DeleteBucketOwnershipControls removed it, which leaves ACLs enabled.
    /// </summary>
    public string? ObjectOwnership { get; set; } = "BucketOwnerEnforced";

    /// <summary>The bucket's ACL grants; null for the default, the owner's <c>FULL_CONTROL</c>.</summary>
    public List<Grant>? Acl { get; set; }

    /// <summary>Where server access logs go, as PutBucketLogging set it; null while logging is off.</summary>
    public LoggingEnabled? Logging { get; set; }

    /// <summary>Whether the bucket's ACLs are disabled (Object Ownership is <c>BucketOwnerEnforced</c>).</summary>
    public bool AclsDisabled => ObjectOwnership == "BucketOwnerEnforced";
}

/// <summary>An object version's ACL, which PutObjectAcl changes in place.</summary>
internal sealed partial class S3ObjectVersion
{
    /// <summary>The version's ACL grants; null for the default, the owner's <c>FULL_CONTROL</c>.</summary>
    public List<Grant>? Acl { get; set; }
}

/// <summary>
/// Object Ownership, bucket and object ACLs (canned ACLs, grant headers and access control policy
/// documents), and server access logging configuration.
/// </summary>
/// <remarks>
/// ACLs are kept and answered for, and checked as S3 checks them when they are written: a bucket whose
/// Object Ownership is <c>BucketOwnerEnforced</c> refuses every ACL but the owner's own, and Block
/// Public Access's <c>BlockPublicAcls</c> refuses a public one first. They grant nothing to other
/// principals: the library models no other accounts, and anonymous access is governed by the bucket
/// policy.
/// </remarks>
internal sealed partial class InternalS3Client
{
    private const string AllUsersGroup = "http://acs.amazonaws.com/groups/global/AllUsers";
    private const string AuthenticatedUsersGroup = "http://acs.amazonaws.com/groups/global/AuthenticatedUsers";
    private const string LogDeliveryGroup = "http://acs.amazonaws.com/groups/s3/LogDelivery";

    private static readonly HashSet<string> ObjectOwnerships = new(StringComparer.Ordinal)
    {
        "BucketOwnerEnforced",
        "BucketOwnerPreferred",
        "ObjectWriter",
    };

    private static readonly HashSet<string> AclPermissions = new(StringComparer.Ordinal)
    {
        "FULL_CONTROL",
        "READ",
        "WRITE",
        "READ_ACP",
        "WRITE_ACP",
    };

    private static readonly HashSet<string> CannedAcls = new(StringComparer.Ordinal)
    {
        "private",
        "public-read",
        "public-read-write",
        "authenticated-read",
        "aws-exec-read",
        "bucket-owner-read",
        "bucket-owner-full-control",
        "log-delivery-write",
    };

    /// <summary>
    /// Applies CreateBucket's Object Ownership and ACL to the new bucket. An ACL granting anyone but
    /// the owner is refused on a bucket whose ACLs are disabled.
    /// </summary>
#pragma warning disable CA1822 // The partial method is declared on the instance.
    partial void OnBucketCreated(S3Bucket bucket, CreateBucketRequest request)
#pragma warning restore CA1822
    {
        if (request.ObjectOwnership is { } ownership)
        {
            if (!ObjectOwnerships.Contains(ownership))
            {
                throw S3Errors.InvalidArgument($"Invalid x-amz-object-ownership header: {ownership}", "x-amz-object-ownership", ownership);
            }

            bucket.ObjectOwnership = ownership;
        }

        var acl = RequestedAcl(request.ACL, request.GrantRead, request.GrantWrite, request.GrantReadACP, request.GrantWriteACP, request.GrantFullControl, null, bucket.OwnerId);
        if (acl is null)
        {
            return;
        }

        if (bucket.AclsDisabled && !GrantsOnlyTo(acl, bucket.OwnerId))
        {
            throw InvalidBucketAclWithObjectOwnership();
        }

        bucket.Acl = acl;
    }

    public override Task<PutBucketOwnershipControlsOutput> PutBucketOwnershipControlsAsync(PutBucketOwnershipControlsRequest request, CancellationToken cancellationToken)
    {
        var rules = request.OwnershipControls?.Rules;
        if (rules is not { Count: 1 } || rules[0].ObjectOwnership is not { } ownership || !ObjectOwnerships.Contains(ownership))
        {
            throw S3Errors.MalformedXml();
        }

        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            if (ownership == "BucketOwnerEnforced" && bucket.Acl is { } acl && !GrantsOnlyTo(acl, bucket.OwnerId))
            {
                throw InvalidBucketAclWithObjectOwnership();
            }

            bucket.ObjectOwnership = ownership;
            return Task.FromResult(new PutBucketOwnershipControlsOutput());
        }
    }

    public override Task<GetBucketOwnershipControlsOutput> GetBucketOwnershipControlsAsync(GetBucketOwnershipControlsRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var ownership = bucket.ObjectOwnership
                ?? throw new S3Exception(
                    "OwnershipControlsNotFoundError",
                    "The bucket ownership controls were not found",
                    HttpStatusCode.NotFound,
                    ("BucketName", bucket.Name));
            return Task.FromResult(new GetBucketOwnershipControlsOutput
            {
                OwnershipControls = new OwnershipControls { Rules = [new OwnershipControlsRule { ObjectOwnership = ownership }] },
            });
        }
    }

    /// <summary>Removes the bucket's Object Ownership setting: ACLs are enabled, as for <c>ObjectWriter</c>.</summary>
    public override Task<DeleteBucketOwnershipControlsOutput> DeleteBucketOwnershipControlsAsync(DeleteBucketOwnershipControlsRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            bucket.ObjectOwnership = null;
            return Task.FromResult(new DeleteBucketOwnershipControlsOutput());
        }
    }

    public override Task<GetBucketAclOutput> GetBucketAclAsync(GetBucketAclRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            return Task.FromResult(new GetBucketAclOutput
            {
                Owner = Owner(bucket.OwnerId),
                Grants = DescribeAcl(bucket.Acl, bucket.OwnerId),
            });
        }
    }

    /// <summary>
    /// Replaces the bucket's ACL. Block Public Access refuses a public ACL first; then a bucket whose
    /// ACLs are disabled refuses any ACL at all.
    /// </summary>
    public override Task<PutBucketAclOutput> PutBucketAclAsync(PutBucketAclRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var acl = RequestedAcl(request.ACL, request.GrantRead, request.GrantWrite, request.GrantReadACP, request.GrantWriteACP, request.GrantFullControl, request.AccessControlPolicy, bucket.OwnerId)
                ?? throw MissingSecurityHeader();
            CheckAclWrite(bucket, acl, "PutBucketAcl", bucket.Arn, refuseAll: true);
            bucket.Acl = acl;
            return Task.FromResult(new PutBucketAclOutput());
        }
    }

    public override Task<GetObjectAclOutput> GetObjectAclAsync(GetObjectAclRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var version = AclVersion(bucket, request.Key, request.VersionId);
            var owner = version.OwnerId.Length > 0 ? version.OwnerId : bucket.OwnerId;
            return Task.FromResult(new GetObjectAclOutput
            {
                Owner = Owner(owner),
                Grants = DescribeAcl(version.Acl, owner),
            });
        }
    }

    /// <summary>
    /// Replaces an object version's ACL, refused as PutBucketAcl is: a public ACL under
    /// <c>BlockPublicAcls</c>, then any ACL on a bucket whose ACLs are disabled.
    /// </summary>
    public override Task<PutObjectAclOutput> PutObjectAclAsync(PutObjectAclRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var key = CheckKey(request.Key);
            var acl = RequestedAcl(request.ACL, request.GrantRead, request.GrantWrite, request.GrantReadACP, request.GrantWriteACP, request.GrantFullControl, request.AccessControlPolicy, bucket.OwnerId)
                ?? throw MissingSecurityHeader();
            CheckAclWrite(bucket, acl, "PutObjectAcl", $"{bucket.Arn}/{key}", refuseAll: true);
            var version = AclVersion(bucket, key, request.VersionId);
            version.Acl = acl;
            OnObjectAclPut(bucket, version);
            return Task.FromResult(new PutObjectAclOutput());
        }
    }

    /// <summary>Called under the store's lock after PutObjectAcl changed a version's ACL (notifications hook in here).</summary>
    partial void OnObjectAclPut(S3Bucket bucket, S3ObjectVersion version);

    /// <summary>
    /// Sets or clears the bucket's server access logging. The target bucket must exist, in the same
    /// region; logs are never written.
    /// </summary>
    public override Task<PutBucketLoggingOutput> PutBucketLoggingAsync(PutBucketLoggingRequest request, CancellationToken cancellationToken)
    {
        var status = request.BucketLoggingStatus ?? throw S3Errors.MalformedXml();
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            if (status.LoggingEnabled is not { } enabled)
            {
                bucket.Logging = null;
                return Task.FromResult(new PutBucketLoggingOutput());
            }

            if (enabled.TargetBucket is not { Length: > 0 } targetName || enabled.TargetPrefix is null)
            {
                throw S3Errors.MalformedXml();
            }

            if (!Store.Buckets.TryGetValue(targetName, out var target))
            {
                throw new S3Exception(
                    "InvalidTargetBucketForLogging",
                    "The target bucket for logging does not exist",
                    HttpStatusCode.BadRequest,
                    ("TargetBucket", targetName));
            }

            if (target.Region != bucket.Region)
            {
                throw new S3Exception(
                    "CrossLocationLoggingProhibitted",
                    "Cross S3 location logging not allowed. ",
                    HttpStatusCode.Forbidden,
                    ("TargetBucketLocation", target.Region),
                    ("SourceBucketLocation", bucket.Region));
            }

            bucket.Logging = new LoggingEnabled
            {
                TargetBucket = targetName,
                TargetPrefix = enabled.TargetPrefix,
                TargetGrants = enabled.TargetGrants,
                TargetObjectKeyFormat = enabled.TargetObjectKeyFormat,
            };
            return Task.FromResult(new PutBucketLoggingOutput());
        }
    }

    public override Task<GetBucketLoggingOutput> GetBucketLoggingAsync(GetBucketLoggingRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            return Task.FromResult(new GetBucketLoggingOutput { LoggingEnabled = bucket.Logging });
        }
    }

    /// <summary>
    /// The ACL a write's <c>x-amz-acl</c> and <c>x-amz-grant-*</c> headers ask for (PutObject, CopyObject
    /// and CreateMultipartUpload), checked as S3 checks it: a public ACL under <c>BlockPublicAcls</c>,
    /// and on a bucket whose ACLs are disabled any ACL granting more than the owner's full control.
    /// </summary>
    private void CheckWriteAclHeaders(S3Bucket bucket, Http.S3WireRequest request, string action, string resource)
    {
        var acl = RequestedAcl(
            request.Header("x-amz-acl"),
            request.Header("x-amz-grant-read"),
            request.Header("x-amz-grant-write"),
            request.Header("x-amz-grant-read-acp"),
            request.Header("x-amz-grant-write-acp"),
            request.Header("x-amz-grant-full-control"),
            null,
            bucket.OwnerId);
        if (acl is not null)
        {
            CheckAclWrite(bucket, acl, action, resource, refuseAll: false);
        }
    }

    /// <summary>
    /// Refuses an ACL write: a public ACL while Block Public Access blocks public ACLs, then, on a
    /// bucket whose ACLs are disabled, any ACL (<paramref name="refuseAll"/>) or one granting anyone but
    /// the owner.
    /// </summary>
    private void CheckAclWrite(S3Bucket bucket, List<Grant> acl, string action, string resource, bool refuseAll)
    {
        if (bucket.PublicAccessBlock?.BlockPublicAcls == true && IsPublicAcl(acl))
        {
            throw S3Errors.AccessDenied(
                $"User: {CallerArn} is not authorized to perform: s3:{action} on resource: \"{resource}\" " +
                "because public ACLs are prevented by the BlockPublicAcls setting in S3 Block Public Access.");
        }

        if (bucket.AclsDisabled && (refuseAll || !GrantsOnlyTo(acl, bucket.OwnerId)))
        {
            throw new S3Exception("AccessControlListNotSupported", "The bucket does not allow ACLs", HttpStatusCode.BadRequest);
        }
    }

    /// <summary>The object version an ACL request addresses: <c>NoSuchKey</c> for a missing key or a deleted one.</summary>
    private static S3ObjectVersion AclVersion(S3Bucket bucket, string? keyName, string? versionId)
    {
        var key = CheckKey(keyName);
        if (versionId is not null)
        {
            CheckVersionIdFormat(versionId);
            var version = bucket.Version(key, versionId) ?? throw S3Errors.NoSuchVersion(key, versionId);
            if (version.IsDeleteMarker)
            {
                throw S3Errors.MethodNotAllowed("GET", "DeleteMarker");
            }

            return version;
        }

        var current = bucket.Current(key);
        return current is null || current.IsDeleteMarker ? throw S3Errors.NoSuchKey(key) : current;
    }

    /// <summary>
    /// The ACL a request asks for, from a canned ACL, grant headers or an access control policy
    /// document; null when it asks for none. A canned ACL and grant headers together are refused.
    /// </summary>
    private static List<Grant>? RequestedAcl(
        string? canned,
        string? read,
        string? write,
        string? readAcp,
        string? writeAcp,
        string? fullControl,
        AccessControlPolicy? policy,
        string ownerId)
    {
        var headerGrants = new (string Permission, string? Value)[]
        {
            ("FULL_CONTROL", fullControl),
            ("READ", read),
            ("WRITE", write),
            ("READ_ACP", readAcp),
            ("WRITE_ACP", writeAcp),
        };
        var hasHeaderGrants = headerGrants.Any(g => g.Value is not null);
        if (canned is not null && hasHeaderGrants)
        {
            throw S3Errors.InvalidRequest("Specifying both Canned ACLs and Header Grants is not allowed");
        }

        if (canned is not null)
        {
            return CannedAcl(canned, ownerId);
        }

        if (hasHeaderGrants)
        {
            var grants = new List<Grant>();
            foreach (var (permission, value) in headerGrants)
            {
                if (value is not null)
                {
                    grants.AddRange(ParseGrantHeader(value).Select(grantee => new Grant { Grantee = grantee, Permission = permission }));
                }
            }

            return grants;
        }

        if (policy is null)
        {
            return null;
        }

        var documented = new List<Grant>();
        foreach (var grant in policy.Grants ?? [])
        {
            if (grant.Permission is not { } permission || !AclPermissions.Contains(permission) || grant.Grantee is not { } grantee)
            {
                throw MalformedAcl();
            }

            documented.Add(new Grant { Grantee = NormalizeGrantee(grantee), Permission = permission });
        }

        return documented;
    }

    /// <summary>The grants a canned ACL stands for.</summary>
    private static List<Grant> CannedAcl(string canned, string ownerId)
    {
        if (!CannedAcls.Contains(canned))
        {
            throw S3Errors.InvalidArgument(string.Empty, "x-amz-acl", canned);
        }

        var grants = new List<Grant> { new() { Grantee = CanonicalUser(ownerId), Permission = "FULL_CONTROL" } };
        switch (canned)
        {
            case "public-read":
                grants.Add(new Grant { Grantee = Group(AllUsersGroup), Permission = "READ" });
                break;
            case "public-read-write":
                grants.Add(new Grant { Grantee = Group(AllUsersGroup), Permission = "READ" });
                grants.Add(new Grant { Grantee = Group(AllUsersGroup), Permission = "WRITE" });
                break;
            case "authenticated-read":
                grants.Add(new Grant { Grantee = Group(AuthenticatedUsersGroup), Permission = "READ" });
                break;
            case "log-delivery-write":
                grants.Add(new Grant { Grantee = Group(LogDeliveryGroup), Permission = "WRITE" });
                grants.Add(new Grant { Grantee = Group(LogDeliveryGroup), Permission = "READ_ACP" });
                break;
        }

        return grants;
    }

    /// <summary>
    /// The grantees of an <c>x-amz-grant-*</c> header: a comma-separated list of <c>id=</c>,
    /// <c>uri=</c> or <c>emailAddress=</c> pairs, each value optionally quoted.
    /// </summary>
    private static List<Grantee> ParseGrantHeader(string value)
    {
        var grantees = new List<Grantee>();
        foreach (var part in value.Split(','))
        {
            var pair = part.Trim();
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                throw S3Errors.InvalidArgument("Argument format not recognized", "x-amz-grant", value);
            }

            var type = pair.Substring(0, eq).Trim();
            var text = pair.Substring(eq + 1).Trim().Trim('"');
            grantees.Add(type switch
            {
                "id" => CanonicalUser(text),
                "uri" => Group(text),
                "emailAddress" => new Grantee { Type = "AmazonCustomerByEmail", EmailAddress = text },
                _ => throw S3Errors.InvalidArgument("Argument format not recognized", "x-amz-grant", value),
            });
        }

        return grantees;
    }

    /// <summary>A grantee from a policy document, as S3 keeps it: its type and the one field that names it.</summary>
    private static Grantee NormalizeGrantee(Grantee grantee) => grantee.Type switch
    {
        "CanonicalUser" when grantee.ID is { Length: > 0 } id => CanonicalUser(id),
        "Group" when grantee.URI is { Length: > 0 } uri => Group(uri),
        "AmazonCustomerByEmail" when grantee.EmailAddress is { Length: > 0 } email => new Grantee { Type = "AmazonCustomerByEmail", EmailAddress = email },
        _ => throw MalformedAcl(),
    };

    /// <summary>The ACL as Get*Acl answers it: the stored grants, or the owner's full control.</summary>
    private static List<Grant> DescribeAcl(List<Grant>? acl, string ownerId) =>
        acl is null
            ? [new Grant { Grantee = CanonicalUser(ownerId), Permission = "FULL_CONTROL" }]
            : [.. acl.Select(g => new Grant { Grantee = NormalizeGrantee(g.Grantee!), Permission = g.Permission })];

    /// <summary>Whether an ACL grants anything to everyone or to every AWS account (a public ACL).</summary>
    private static bool IsPublicAcl(List<Grant> acl) =>
        acl.Exists(g => g.Grantee?.Type == "Group" && g.Grantee.URI is AllUsersGroup or AuthenticatedUsersGroup);

    /// <summary>Whether an ACL grants nothing to anyone but <paramref name="ownerId"/>.</summary>
    private static bool GrantsOnlyTo(List<Grant> acl, string ownerId) =>
        acl.TrueForAll(g => g.Grantee?.Type == "CanonicalUser" && g.Grantee.ID == ownerId);

    private static Grantee CanonicalUser(string id) => new() { Type = "CanonicalUser", ID = id };

    private static Grantee Group(string uri) => new() { Type = "Group", URI = uri };

    private static S3Exception MalformedAcl() =>
        new("MalformedACLError", "The XML you provided was not well-formed or did not validate against our published schema", HttpStatusCode.BadRequest);

    private static S3Exception MissingSecurityHeader() =>
        new("MissingSecurityHeader", "Your request was missing a required header", HttpStatusCode.BadRequest, ("MissingHeaderName", "x-amz-acl"));

    private static S3Exception InvalidBucketAclWithObjectOwnership() =>
        new("InvalidBucketAclWithObjectOwnership", "Bucket cannot have ACLs set with ObjectOwnership's BucketOwnerEnforced setting", HttpStatusCode.BadRequest);
}
