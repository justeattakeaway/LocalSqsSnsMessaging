using System.Net;

namespace InMemoryAws.S3;

/// <summary>
/// What S3 checks about who sends a request, once the bucket is found: the expected bucket owner,
/// Requester Pays, the bucket policy's explicit Deny statements, the bucket's block on SSE-C
/// uploads, a write's ACL and website redirect headers, and the SSE-C keys a copy source or an
/// upload's part needs.
/// </summary>
/// <remarks>
/// <para>
/// As for the other services' resource policies (<see cref="ResourcePolicy"/>), only explicit Deny
/// statements are enforced: a bucket with no policy, or one that only allows, serves every request as
/// before. The library models no IAM, so every signed request acts for the cloud's account (its root
/// user, as <c>sts:GetCallerIdentity</c> answers). The bucket owner's root user can always read,
/// set and delete the bucket policy, so a policy can't lock it out.
/// </para>
/// <para>
/// An unsigned request is served as the account, as the other services serve one, except where the
/// bucket policy grants the action to everyone (<c>"Principal": "*"</c>) and the request meets that
/// grant's conditions: such a request is the anonymous access the grant exists for. An unsigned
/// request to a Requester Pays bucket is always anonymous, and refused.
/// </para>
/// </remarks>
internal sealed partial class InternalS3Client
{
    /// <summary>The bucket policy actions the bucket owner's root user is never denied.</summary>
    private static readonly HashSet<string> OwnerPolicyActions = new(StringComparer.Ordinal)
    {
        "GetBucketPolicy",
        "PutBucketPolicy",
        "DeleteBucketPolicy",
    };

    /// <summary>The operations whose action and resource are an object's, and the action each needs.</summary>
    private static readonly Dictionary<string, string> ObjectActions = new(StringComparer.Ordinal)
    {
        ["GetObject"] = "GetObject",
        ["HeadObject"] = "GetObject",
        ["GetObjectAttributes"] = "GetObjectAttributes",
        ["PutObject"] = "PutObject",
        ["CopyObject"] = "PutObject",
        ["CreateMultipartUpload"] = "PutObject",
        ["UploadPart"] = "PutObject",
        ["UploadPartCopy"] = "PutObject",
        ["CompleteMultipartUpload"] = "PutObject",
        ["AbortMultipartUpload"] = "AbortMultipartUpload",
        ["ListParts"] = "ListMultipartUploadParts",
        ["DeleteObject"] = "DeleteObject",
        ["GetObjectTagging"] = "GetObjectTagging",
        ["PutObjectTagging"] = "PutObjectTagging",
        ["DeleteObjectTagging"] = "DeleteObjectTagging",
        ["GetObjectAcl"] = "GetObjectAcl",
        ["PutObjectAcl"] = "PutObjectAcl",
        ["GetObjectRetention"] = "GetObjectRetention",
        ["PutObjectRetention"] = "PutObjectRetention",
        ["GetObjectLegalHold"] = "GetObjectLegalHold",
        ["PutObjectLegalHold"] = "PutObjectLegalHold",
        ["RestoreObject"] = "RestoreObject",
        ["SelectObjectContent"] = "GetObject",
        ["GetObjectTorrent"] = "GetObjectTorrent",
    };

    /// <summary>The bucket operations whose action is not named after the operation.</summary>
    private static readonly Dictionary<string, string> BucketActions = new(StringComparer.Ordinal)
    {
        ["HeadBucket"] = "ListBucket",
        ["ListObjects"] = "ListBucket",
        ["ListObjectsV2"] = "ListBucket",
        ["ListObjectVersions"] = "ListBucketVersions",
        ["ListMultipartUploads"] = "ListBucketMultipartUploads",
        ["DeleteObjects"] = "DeleteObject",
        ["GetPublicAccessBlock"] = "GetBucketPublicAccessBlock",
        ["PutPublicAccessBlock"] = "PutBucketPublicAccessBlock",
        ["DeletePublicAccessBlock"] = "PutBucketPublicAccessBlock",
        ["GetBucketEncryption"] = "GetEncryptionConfiguration",
        ["PutBucketEncryption"] = "PutEncryptionConfiguration",
        ["DeleteBucketEncryption"] = "PutEncryptionConfiguration",
        ["GetBucketReplication"] = "GetReplicationConfiguration",
        ["PutBucketReplication"] = "PutReplicationConfiguration",
        ["DeleteBucketReplication"] = "PutReplicationConfiguration",
        ["DeleteBucketWebsite"] = "DeleteBucketWebsite",
        ["GetBucketInventoryConfiguration"] = "GetInventoryConfiguration",
        ["ListBucketInventoryConfigurations"] = "GetInventoryConfiguration",
        ["PutBucketInventoryConfiguration"] = "PutInventoryConfiguration",
        ["DeleteBucketInventoryConfiguration"] = "PutInventoryConfiguration",
        ["GetBucketLifecycleConfiguration"] = "GetLifecycleConfiguration",
        ["PutBucketLifecycleConfiguration"] = "PutLifecycleConfiguration",
        ["DeleteBucketLifecycle"] = "PutLifecycleConfiguration",
        ["GetBucketCors"] = "GetBucketCORS",
        ["PutBucketCors"] = "PutBucketCORS",
        ["DeleteBucketCors"] = "PutBucketCORS",
        ["DeleteBucketTagging"] = "PutBucketTagging",
        ["GetBucketNotificationConfiguration"] = "GetBucketNotification",
        ["PutBucketNotificationConfiguration"] = "PutBucketNotification",
        ["GetObjectLockConfiguration"] = "GetBucketObjectLockConfiguration",
        ["PutObjectLockConfiguration"] = "PutBucketObjectLockConfiguration",
        ["DeleteBucketOwnershipControls"] = "PutBucketOwnershipControls",
        ["GetBucketAccelerateConfiguration"] = "GetAccelerateConfiguration",
        ["PutBucketAccelerateConfiguration"] = "PutAccelerateConfiguration",
        ["GetBucketAnalyticsConfiguration"] = "GetAnalyticsConfiguration",
        ["ListBucketAnalyticsConfigurations"] = "GetAnalyticsConfiguration",
        ["PutBucketAnalyticsConfiguration"] = "PutAnalyticsConfiguration",
        ["DeleteBucketAnalyticsConfiguration"] = "PutAnalyticsConfiguration",
        ["GetBucketMetricsConfiguration"] = "GetMetricsConfiguration",
        ["ListBucketMetricsConfigurations"] = "GetMetricsConfiguration",
        ["PutBucketMetricsConfiguration"] = "PutMetricsConfiguration",
        ["DeleteBucketMetricsConfiguration"] = "PutMetricsConfiguration",
        ["GetBucketIntelligentTieringConfiguration"] = "GetIntelligentTieringConfiguration",
        ["ListBucketIntelligentTieringConfigurations"] = "GetIntelligentTieringConfiguration",
        ["PutBucketIntelligentTieringConfiguration"] = "PutIntelligentTieringConfiguration",
        ["DeleteBucketIntelligentTieringConfiguration"] = "PutIntelligentTieringConfiguration",
        ["GetBucketLoggingConfiguration"] = "GetBucketLogging",
        ["PostObject"] = "PutObject",
    };

    /// <summary>The ARN of the caller every signed request acts as: the cloud account's root user.</summary>
    private string CallerArn => $"arn:aws:iam::{_cloud.CurrentAccountId}:root";

    /// <summary>
    /// Whether the request carries a signature: a SigV4 (or SigV2) <c>Authorization</c> header, or a
    /// presigned URL's credential.
    /// </summary>
    private bool IsSignedRequest =>
        Request is not { } request
        || request.Header("Authorization") is not null
        || request.Query("X-Amz-Credential") is not null
        || request.Query("AWSAccessKeyId") is not null;

    /// <summary>
    /// Checks the request against the bucket it names, right after the bucket was found: the expected
    /// bucket owner, Requester Pays, the bucket policy, and the SSE-C upload block. A request that
    /// didn't come over HTTP, or that names another bucket (a copy's source), is not checked here.
    /// </summary>
    private void Authorize(S3Bucket bucket)
    {
        if (Request is not { Operation: { } operation } request || !string.Equals(request.Bucket, bucket.Name, StringComparison.Ordinal))
        {
            return;
        }

        CheckExpectedOwner(request.Header("x-amz-expected-bucket-owner"));
        var (action, resource) = ActionOf(operation, bucket, request);
        if (bucket.Payer == "Requester" && !IsSignedRequest)
        {
            throw S3Errors.AccessDenied("Anonymous users cannot invoke requests against Requester Pays buckets. Please authenticate.");
        }

        if (!OwnerPolicyActions.Contains(action))
        {
            DenyExplicitly(bucket, action, resource);
        }

        if (operation is "PutObject" or "CopyObject" or "CreateMultipartUpload")
        {
            CheckWriteAclHeaders(bucket, request, action, resource);
            CheckWebsiteRedirectLocation(request.Header("x-amz-website-redirect-location"));
        }

        if (action == "PutObject" && operation is "PutObject" or "CopyObject" or "CreateMultipartUpload"
            && request.Header("x-amz-server-side-encryption-customer-algorithm") is not null
            && bucket.BlocksSseC && !bucket.IsDirectory)
        {
            throw S3Errors.AccessDenied(
                $"User: {CallerArn} is not authorized to perform: s3:PutObject on resource: \"{resource}\" " +
                "because this bucket has blocked upload requests that specify Server Side Encryption with Customer provided keys (SSE-C). " +
                "Please specify a different server-side encryption type.");
        }

        if (operation is "CopyObject" or "UploadPartCopy")
        {
            AuthorizeCopySource(request);
        }

        if (operation is "UploadPart" or "UploadPartCopy"
            && request.Query("uploadId") is { } uploadId
            && bucket.Uploads.TryGetValue(uploadId, out var upload))
        {
            CheckUploadSseCustomerKey(upload, request);
        }
    }

    /// <summary>A copy reads its source: the source bucket's policy must not deny <c>s3:GetObject</c> on it.</summary>
    private void AuthorizeCopySource(Http.S3WireRequest request)
    {
        CheckExpectedOwner(request.Header("x-amz-source-expected-bucket-owner"));
        var (sourceBucket, sourceKey, sourceVersionId) = ParseCopySource(request.Header("x-amz-copy-source"));
        if (Store.Buckets.TryGetValue(sourceBucket, out var source))
        {
            DenyExplicitly(source, sourceVersionId is null ? "GetObject" : "GetObjectVersion", $"{source.Arn}/{sourceKey}");
            var version = sourceVersionId is null ? source.Current(sourceKey) : source.Version(sourceKey, sourceVersionId);
            if (version is { IsDeleteMarker: false })
            {
                CheckCopySourceSseCustomerKey(version, request);
            }
        }
    }

    /// <summary>
    /// <c>x-amz-expected-bucket-owner</c>: a request that names another account as the bucket's owner
    /// is refused, whoever sends it, so a caller can't be tricked into using another account's bucket.
    /// </summary>
    private void CheckExpectedOwner(string? expectedOwner)
    {
        if (expectedOwner is not null && !string.Equals(expectedOwner, _cloud.CurrentAccountId, StringComparison.Ordinal))
        {
            throw S3Errors.AccessDenied();
        }
    }

    /// <summary>
    /// Refuses the request when the bucket policy explicitly denies <c>s3:</c><paramref name="action"/>
    /// on <paramref name="resource"/> to the caller.
    /// </summary>
    private void DenyExplicitly(S3Bucket bucket, string action, string resource)
    {
        if (ResourcePolicy.Parse(ResourcePolicyKind.Bucket, bucket.Policy) is not { } policy)
        {
            return;
        }

        var asAccount = PolicyRequest(action, resource);
        var anonymous = !IsSignedRequest && GrantsAnonymously(bucket, policy, asAccount with { PrincipalAccount = null, PrincipalArn = null });
        var context = anonymous ? asAccount with { PrincipalAccount = null, PrincipalArn = null } : asAccount;
        if (policy.DeniesExplicitly(context))
        {
            throw S3Errors.AccessDenied(
                $"User: {(anonymous ? "anonymous" : CallerArn)} is not authorized to perform: s3:{action} on resource: \"{resource}\" " +
                "with an explicit deny in a resource-based policy");
        }
    }

    /// <summary>
    /// Whether the policy admits an anonymous request: a grant to everyone the request meets, and no
    /// explicit Deny; while Block Public Access restricts public buckets, a public policy's grants
    /// admit nobody anonymously.
    /// </summary>
    private static bool GrantsAnonymously(S3Bucket bucket, ResourcePolicy policy, ResourcePolicy.RequestContext anonymous) =>
        policy.GrantsToEveryone(anonymous)
        && !policy.DeniesExplicitly(anonymous)
        && !(bucket.PublicAccessBlock?.RestrictPublicBuckets == true && policy.IsPublic());

    /// <summary>
    /// The request as policy evaluation sees it: the caller (the account's root user, or nobody) and
    /// the condition keys S3 adds from the request's headers and query. As for every service,
    /// <c>aws:SecureTransport</c> is always true, so a deny-unless-TLS guardrail (CDK's
    /// <c>enforceSSL</c>) doesn't lock out a client of the plain-HTTP server.
    /// </summary>
    private ResourcePolicy.RequestContext PolicyRequest(string action, string resource)
    {
        var request = Request;
        var keys = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["aws:Referer"] = request?.Header("Referer"),
            ["aws:UserAgent"] = request?.Header("User-Agent"),
            ["s3:if-match"] = request?.Header("If-Match"),
            ["s3:if-none-match"] = request?.Header("If-None-Match"),
            ["s3:x-amz-acl"] = request?.Header("x-amz-acl"),
            ["s3:x-amz-server-side-encryption"] = request?.Header("x-amz-server-side-encryption"),
            ["s3:x-amz-server-side-encryption-aws-kms-key-id"] = request?.Header("x-amz-server-side-encryption-aws-kms-key-id"),
            ["s3:x-amz-server-side-encryption-customer-algorithm"] = request?.Header("x-amz-server-side-encryption-customer-algorithm"),
            ["s3:x-amz-storage-class"] = request?.Header("x-amz-storage-class"),
            ["s3:x-amz-content-sha256"] = request?.Header("x-amz-content-sha256"),
            ["s3:x-amz-copy-source"] = request?.Header("x-amz-copy-source"),
            ["s3:x-amz-metadata-directive"] = request?.Header("x-amz-metadata-directive"),
            ["s3:prefix"] = request?.Query("prefix"),
            ["s3:delimiter"] = request?.Query("delimiter"),
            ["s3:max-keys"] = request?.Query("max-keys"),
            ["s3:VersionId"] = request?.Query("versionId"),
            ["s3:ResourceAccount"] = _cloud.CurrentAccountId,
        };
        return new ResourcePolicy.RequestContext(
            action,
            resource,
            _cloud.CurrentAccountId,
            CallerArn,
            SourceIp: IPAddress.Loopback)
        {
            ServiceKeys = keys,
        };
    }

    /// <summary>The action an operation needs, and the resource it acts on: the object's ARN, or the bucket's.</summary>
    private static (string Action, string Resource) ActionOf(string operation, S3Bucket bucket, Http.S3WireRequest request)
    {
        if (ObjectActions.TryGetValue(operation, out var objectAction) && request.Key is { } key)
        {
            if (request.Query("versionId") is not null && objectAction is "GetObject" or "DeleteObject" or "GetObjectTagging" or "PutObjectTagging" or "DeleteObjectTagging" or "GetObjectAcl" or "PutObjectAcl")
            {
                objectAction += "Version";
            }

            return (objectAction, $"{bucket.Arn}/{key}");
        }

        return (BucketActions.TryGetValue(operation, out var bucketAction) ? bucketAction : operation, bucket.Arn);
    }
}
