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

internal class InternalQueueDoesNotExistException : AwsServiceException
{
    public InternalQueueDoesNotExistException(string message) : base(message)
    {
        ErrorCode = "QueueDoesNotExist";
    }
}

internal class InternalReceiptHandleIsInvalidException : AwsServiceException
{
    public InternalReceiptHandleIsInvalidException(string message) : base(message)
    {
        ErrorCode = "ReceiptHandleIsInvalid";
    }
}

internal class SqsServiceException : AwsServiceException
{
    public SqsServiceException(string message) : base(message) { }
}

internal class InternalResourceNotFoundException : AwsServiceException
{
    public InternalResourceNotFoundException(string message) : base(message)
    {
        ErrorCode = "ResourceNotFoundException";
    }
}

internal class InternalUnsupportedOperationException : AwsServiceException
{
    public InternalUnsupportedOperationException(string message) : base(message)
    {
        ErrorCode = "UnsupportedOperation";
    }
}

internal class InternalBatchRequestTooLongException : AwsServiceException
{
    public InternalBatchRequestTooLongException(string message) : base(message)
    {
        ErrorCode = "BatchRequestTooLong";
    }
}

// SNS exceptions

internal class InternalNotFoundException : AwsServiceException
{
    public InternalNotFoundException(string message) : base(message)
    {
        ErrorCode = "NotFound";
        StatusCode = HttpStatusCode.NotFound;
    }
}

internal class InternalInvalidParameterException : AwsServiceException
{
    public InternalInvalidParameterException(string message) : base(message)
    {
        ErrorCode = "InvalidParameter";
    }
}

// EventBridge exceptions

internal class InternalResourceAlreadyExistsException : AwsServiceException
{
    public InternalResourceAlreadyExistsException(string message) : base(message)
    {
        ErrorCode = "ResourceAlreadyExistsException";
    }
}

internal class InternalInvalidEventPatternException : AwsServiceException
{
    public InternalInvalidEventPatternException(string message) : base(message)
    {
        ErrorCode = "InvalidEventPatternException";
    }
}

// Alias for SNS InternalBatchRequestTooLongException - same class works for both SQS and SNS
// since the SQS one above is sufficient (both use the same error pattern)
