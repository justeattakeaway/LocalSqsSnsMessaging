using System.Net;

#pragma warning disable CA1032 // Implement standard exception constructors - internal types, only need message constructor
#pragma warning disable CA1064 // Exceptions should be public - these are internal implementation exceptions

namespace LocalSqsSnsMessaging;

/// <summary>
/// Base exception for AWS service errors. Provides ErrorCode and StatusCode
/// properties used by the error response serializers.
/// </summary>
internal class AwsServiceException : Exception
{
    public AwsServiceException(string message) : base(message) { }
    public AwsServiceException(string message, Exception innerException) : base(message, innerException) { }

    public string? ErrorCode { get; init; }
    public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.BadRequest;
}

// SQS exceptions

internal class QueueDoesNotExistException : AwsServiceException
{
    public QueueDoesNotExistException(string message) : base(message)
    {
        ErrorCode = "QueueDoesNotExist";
    }
}

internal class ReceiptHandleIsInvalidException : AwsServiceException
{
    public ReceiptHandleIsInvalidException(string message) : base(message)
    {
        ErrorCode = "ReceiptHandleIsInvalid";
    }
}

internal class SqsServiceException : AwsServiceException
{
    public SqsServiceException(string message) : base(message) { }
}

internal class ResourceNotFoundException : AwsServiceException
{
    public ResourceNotFoundException(string message) : base(message)
    {
        ErrorCode = "ResourceNotFoundException";
    }
}

internal class UnsupportedOperationException : AwsServiceException
{
    public UnsupportedOperationException(string message) : base(message)
    {
        ErrorCode = "UnsupportedOperation";
    }
}

internal class BatchRequestTooLongException : AwsServiceException
{
    public BatchRequestTooLongException(string message) : base(message)
    {
        ErrorCode = "BatchRequestTooLong";
    }
}

// SNS exceptions

internal class NotFoundException : AwsServiceException
{
    public NotFoundException(string message) : base(message)
    {
        ErrorCode = "NotFound";
        StatusCode = HttpStatusCode.NotFound;
    }
}

internal class InvalidParameterException : AwsServiceException
{
    public InvalidParameterException(string message) : base(message)
    {
        ErrorCode = "InvalidParameter";
    }
}

// Alias for SNS BatchRequestTooLongException - same class works for both SQS and SNS
// since the SQS one above is sufficient (both use the same error pattern)
