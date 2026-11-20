#pragma warning disable SA1402 // One interface and the generated models that implement it.
#pragma warning disable SA1649

namespace InMemoryAws.S3.Model;

/// <summary>
/// The Object Lock headers (<c>x-amz-object-lock-*</c>) of the writes that take them and the reads
/// that answer them, so one helper reads and writes them whichever model it is.
/// </summary>
internal interface IS3ObjectLockMembers
{
    string? ObjectLockMode { get; set; }

    DateTime? ObjectLockRetainUntilDate { get; set; }

    string? ObjectLockLegalHoldStatus { get; set; }

    string? ObjectLockEventHold { get; set; }

    int? ObjectLockEventHoldDurationDays { get; set; }

    int? ObjectLockEventHoldDurationYears { get; set; }
}

internal sealed partial class PutObjectRequest : IS3ObjectLockMembers;

internal sealed partial class CopyObjectRequest : IS3ObjectLockMembers;

internal sealed partial class CreateMultipartUploadRequest : IS3ObjectLockMembers;

internal sealed partial class GetObjectOutput : IS3ObjectLockMembers;

internal sealed partial class HeadObjectOutput : IS3ObjectLockMembers;
