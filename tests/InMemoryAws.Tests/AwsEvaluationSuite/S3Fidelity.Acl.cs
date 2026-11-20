using Amazon.S3;
using Amazon.S3.Model;

namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>What the S3 ACL, Object Ownership and logging ports share.</summary>
public static partial class S3Fidelity
{
    /// <summary>The URI of the group of every caller, signed or not.</summary>
    public const string AllUsers = "http://acs.amazonaws.com/groups/global/AllUsers";

    /// <summary>The URI of S3's log delivery group.</summary>
    public const string LogDelivery = "http://acs.amazonaws.com/groups/s3/LogDelivery";

    /// <summary>
    /// <c>_new_bucket(ctx, s3, **kwargs)</c> and <c>_create_bucket(ctx, s3, name, **kwargs)</c>: a bucket
    /// named <paramref name="prefix"/> plus 12 hex digits, created with the region's
    /// <c>LocationConstraint</c> and, when given, <c>ObjectOwnership</c>; emptied and deleted at cleanup.
    /// </summary>
    public static async Task<string> NewBucket(FidelityContext ctx, string prefix = "sim2real-behav-bkt-", ObjectOwnership? objectOwnership = null, string? region = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        region ??= ctx.Region;
        var s3 = ctx.S3Client(region);
        var name = FidelityContext.UniqueName(prefix);
        var request = new PutBucketRequest { BucketName = name, UseClientRegion = false, ObjectOwnership = objectOwnership };
        if (region != "us-east-1")
        {
            request.PutBucketConfiguration = new PutBucketConfiguration { LocationConstraint = new BucketLocationConstraint(region) };
        }

        await s3.PutBucketAsync(request);
        ctx.OnCleanup(() => EmptyAndDelete(s3, name));
        return name;
    }

    /// <summary>A grant list as Python's <c>repr</c> of boto3's <c>Grants</c> shows it, for comparing and for messages.</summary>
    public static string Describe(IEnumerable<S3Grant>? grants) =>
        "[" + string.Join(", ", (grants ?? []).Select(g =>
        {
            var grantee = new List<string>();
            if (g.Grantee?.CanonicalUser is { } id)
            {
                grantee.Add($"'ID': {Py.Repr(id)}");
            }

            if (g.Grantee?.URI is { } uri)
            {
                grantee.Add($"'URI': {Py.Repr(uri)}");
            }

            grantee.Add($"'Type': {Py.Repr(g.Grantee?.Type?.Value)}");
            return $"{{'Grantee': {{{string.Join(", ", grantee)}}}, 'Permission': {Py.Repr(g.Permission?.Value)}}}";
        })) + "]";

    /// <summary><c>g["Grantee"].get("Type")</c>.</summary>
    public static string? GranteeType(S3Grant grant) => grant?.Grantee?.Type?.Value;

    /// <summary><c>_has_public_grant(grants)</c>: whether any grant is to the AllUsers group.</summary>
    public static bool HasPublicGrant(IEnumerable<S3Grant>? grants) =>
        (grants ?? []).Any(g => GranteeType(g) == "Group" && g.Grantee.URI == AllUsers);

    /// <summary><c>s3.get_bucket_acl(Bucket=...)["Grants"]</c>.</summary>
    public static async Task<List<S3Grant>> BucketGrants(IAmazonS3 s3, string bucket)
    {
        ArgumentNullException.ThrowIfNull(s3);
        return (await s3.GetBucketAclAsync(new GetBucketAclRequest { BucketName = bucket })).Grants ?? [];
    }

    /// <summary><c>s3.get_object_acl(Bucket=..., Key=...)["Grants"]</c>.</summary>
    public static async Task<List<S3Grant>> ObjectGrants(IAmazonS3 s3, string bucket, string key)
    {
        ArgumentNullException.ThrowIfNull(s3);
        return (await s3.GetObjectAclAsync(new GetObjectAclRequest { BucketName = bucket, Key = key })).Grants ?? [];
    }

    /// <summary>A grant of <paramref name="permission"/> to a canonical user.</summary>
    public static S3Grant UserGrant(string id, S3Permission permission) =>
        new() { Grantee = new S3Grantee { CanonicalUser = id }, Permission = permission };

    /// <summary>A grant of <paramref name="permission"/> to a group.</summary>
    public static S3Grant GroupGrant(string uri, S3Permission permission) =>
        new() { Grantee = new S3Grantee { URI = uri }, Permission = permission };

    /// <summary><c>{"BlockPublicAcls": ..., ...}</c> as a Block Public Access configuration.</summary>
    public static PublicAccessBlockConfiguration Block(bool acls = false, bool ignoreAcls = false, bool policy = false, bool restrict = false) =>
        new() { BlockPublicAcls = acls, IgnorePublicAcls = ignoreAcls, BlockPublicPolicy = policy, RestrictPublicBuckets = restrict };

    /// <summary>The message S3 refuses a public ACL with while Block Public Access blocks public ACLs.</summary>
    public static string PublicAclRefusal(FidelityContext ctx, string action, string resource)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return $"User: {ctx.CallerArn} is not authorized to perform: s3:{action} on resource: \"{resource}\" " +
            "because public ACLs are prevented by the BlockPublicAcls setting in S3 Block Public Access.";
    }
}
