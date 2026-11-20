using System.Net;

#pragma warning disable CA1032 // Implement standard exception constructors - internal type
#pragma warning disable CA1064 // Exceptions should be public - internal implementation exception

namespace InMemoryAws.S3;

/// <summary>
/// An S3 error: its code, message and status, and the elements S3 adds to its error document after
/// the message (<c>BucketName</c>, <c>Key</c>, <c>ArgumentName</c>, ...), in order. Some errors also
/// carry response headers (<c>x-amz-bucket-region</c>, <c>x-amz-delete-marker</c>).
/// </summary>
internal sealed class S3Exception : AwsServiceException
{
    public S3Exception(string code, string message, HttpStatusCode status, params (string Name, string? Value)[] details)
        : base(message)
    {
        ErrorCode = code;
        StatusCode = status;
        foreach (var (name, value) in details)
        {
            if (value is not null)
            {
                Details.Add(new KeyValuePair<string, string>(name, value));
            }
        }
    }

    /// <summary>The elements after <c>Message</c> in the error document.</summary>
    public List<KeyValuePair<string, string>> Details { get; } = [];

    /// <summary>Headers S3 sends with this error.</summary>
    public List<KeyValuePair<string, string>> Headers { get; } = [];

    public S3Exception WithHeader(string name, string value)
    {
        Headers.Add(new KeyValuePair<string, string>(name, value));
        return this;
    }
}

/// <summary>S3's errors, with the codes, messages and statuses S3 answers them with.</summary>
internal static class S3Errors
{
    public static S3Exception NoSuchBucket(string? bucket) =>
        new("NoSuchBucket", "The specified bucket does not exist", HttpStatusCode.NotFound, ("BucketName", bucket));

    public static S3Exception NoSuchKey(string? key) =>
        new("NoSuchKey", "The specified key does not exist.", HttpStatusCode.NotFound, ("Key", key));

    public static S3Exception NoSuchVersion(string? key, string? versionId) =>
        new("NoSuchVersion", "The specified version does not exist.", HttpStatusCode.NotFound, ("Key", key), ("VersionId", versionId));

    public static S3Exception NoSuchUpload(string? uploadId) =>
        new("NoSuchUpload", "The specified upload does not exist. The upload ID may be invalid, or the upload may have been aborted or completed.", HttpStatusCode.NotFound, ("UploadId", uploadId));

    public static S3Exception BucketAlreadyOwnedByYou(string bucket) =>
        new("BucketAlreadyOwnedByYou", "Your previous request to create the named bucket succeeded and you already own it.", HttpStatusCode.Conflict, ("BucketName", bucket));

    public static S3Exception BucketAlreadyExists(string bucket) =>
        new("BucketAlreadyExists", "The requested bucket name is not available. The bucket namespace is shared by all users of the system. Please select a different name and try again.", HttpStatusCode.Conflict, ("BucketName", bucket));

    /// <summary>A bucket that still holds objects; a versioned bucket's refusal names the versions too.</summary>
    public static S3Exception BucketNotEmpty(string bucket, bool versioned = false) =>
        new("BucketNotEmpty", versioned ? "The bucket you tried to delete is not empty. You must delete all versions in the bucket." : "The bucket you tried to delete is not empty", HttpStatusCode.Conflict, ("BucketName", bucket));

    public static S3Exception InvalidBucketName(string? bucket) =>
        new("InvalidBucketName", "The specified bucket is not valid.", HttpStatusCode.BadRequest, ("BucketName", bucket));

    public static S3Exception InvalidArgument(string message, string? name = null, string? value = null) =>
        new("InvalidArgument", message, HttpStatusCode.BadRequest, ("ArgumentName", name), ("ArgumentValue", value));

    public static S3Exception InvalidRequest(string message) =>
        new("InvalidRequest", message, HttpStatusCode.BadRequest);

    public static S3Exception MalformedXml() =>
        new("MalformedXML", "The XML you provided was not well-formed or did not validate against our published schema", HttpStatusCode.BadRequest);

    /// <summary>An operation, or a feature of one, the cloud doesn't emulate.</summary>
    public static S3Exception NotImplemented(string? operation = null)
    {
        _ = operation;
        return new("NotImplemented", "A header you provided implies functionality that is not implemented", HttpStatusCode.NotImplemented);
    }

    public static S3Exception MethodNotAllowed(string method, string resourceType) =>
        new("MethodNotAllowed", "The specified method is not allowed against this resource.", HttpStatusCode.MethodNotAllowed, ("Method", method), ("ResourceType", resourceType));

    public static S3Exception PreconditionFailed(string condition) =>
        new("PreconditionFailed", "At least one of the pre-conditions you specified did not hold", HttpStatusCode.PreconditionFailed, ("Condition", condition));

    public static S3Exception NotModified() =>
        new("NotModified", "Not Modified", HttpStatusCode.NotModified);

    public static S3Exception InvalidRange(string? range, long size) =>
        new("InvalidRange", "The requested range is not satisfiable", HttpStatusCode.RequestedRangeNotSatisfiable, ("RangeRequested", range), ("ActualObjectSize", size.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public static S3Exception AccessDenied(string message = "Access Denied") =>
        new("AccessDenied", message, HttpStatusCode.Forbidden);

    public static S3Exception BadDigest(string message = "The Content-MD5 you specified did not match what we received.") =>
        new("BadDigest", message, HttpStatusCode.BadRequest);

    public static S3Exception InvalidDigest() =>
        new("InvalidDigest", "The Content-MD5 you specified is not valid.", HttpStatusCode.BadRequest);

    public static S3Exception InvalidPart(string uploadId, int partNumber, string? etag) =>
        new("InvalidPart", "One or more of the specified parts could not be found.  The part may not have been uploaded, or the specified entity tag may not match the part's entity tag.", HttpStatusCode.BadRequest,
            ("UploadId", uploadId), ("PartNumber", partNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("ETag", etag));

    public static S3Exception InvalidPartOrder(string uploadId) =>
        new("InvalidPartOrder", "The list of parts was not in ascending order. Parts must be ordered by part number.", HttpStatusCode.BadRequest, ("UploadId", uploadId));

    public static S3Exception EntityTooSmall(long proposedSize, int partNumber, string etag) =>
        new("EntityTooSmall", "Your proposed upload is smaller than the minimum allowed size", HttpStatusCode.BadRequest,
            ("ProposedSize", proposedSize.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("MinSizeAllowed", "5242880"),
            ("PartNumber", partNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("ETag", etag));

    public static S3Exception EntityTooLarge(long proposedSize, long maxSize) =>
        new("EntityTooLarge", "Your proposed upload exceeds the maximum allowed size", HttpStatusCode.BadRequest,
            ("ProposedSize", proposedSize.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("MaxSizeAllowed", maxSize.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public static S3Exception NoSuchTagSet() =>
        new("NoSuchTagSet", "The TagSet does not exist", HttpStatusCode.NotFound);

    public static S3Exception NoSuchConfiguration(string code, string message, string bucket) =>
        new(code, message, HttpStatusCode.NotFound, ("BucketName", bucket));

    public static S3Exception IllegalLocationConstraint(string? constraint) =>
        new("IllegalLocationConstraintException",
            constraint is null
                ? "The unspecified location constraint is incompatible for the region specific endpoint this request was sent to."
                : $"The {constraint} location constraint is incompatible for the region specific endpoint this request was sent to.",
            HttpStatusCode.BadRequest);

    public static S3Exception InvalidLocationConstraint(string constraint) =>
        new("InvalidLocationConstraint", "The specified location-constraint is not valid", HttpStatusCode.BadRequest, ("LocationConstraint", constraint));
}
