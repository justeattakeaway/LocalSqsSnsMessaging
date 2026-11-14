using System.Net;

namespace LocalSqsSnsMessaging.Http;

/// <summary>
/// SQS is <c>awsQueryCompatible</c>: over the JSON protocol every error also carries an
/// <c>x-amzn-query-error: &lt;code&gt;;&lt;Sender|Receiver&gt;</c> header naming the error code the
/// legacy Query protocol used, and AWS SDKs report that code (for example
/// <c>AWS.SimpleQueueService.NonExistentQueue</c> rather than <c>QueueDoesNotExist</c>).
/// </summary>
internal static class SqsQueryCompatibleErrors
{
    /// <summary>The header SQS sends the Query-protocol error code in.</summary>
    public const string HeaderName = "x-amzn-query-error";

    /// <summary>
    /// JSON error code to Query error code, from the <c>aws.protocols#awsQueryError</c> traits in
    /// AWS's Smithy model for SQS (github.com/aws/api-models-aws, models/sqs/service/2012-11-05).
    /// An error without the trait keeps its JSON code.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> QueryErrorCodes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["BatchEntryIdsNotDistinct"] = "AWS.SimpleQueueService.BatchEntryIdsNotDistinct",
        ["BatchRequestTooLong"] = "AWS.SimpleQueueService.BatchRequestTooLong",
        ["EmptyBatchRequest"] = "AWS.SimpleQueueService.EmptyBatchRequest",
        ["InvalidAddress"] = "InvalidAddress",
        ["InvalidBatchEntryId"] = "AWS.SimpleQueueService.InvalidBatchEntryId",
        ["InvalidSecurity"] = "InvalidSecurity",
        ["KmsAccessDenied"] = "KMS.AccessDeniedException",
        ["KmsDisabled"] = "KMS.DisabledException",
        ["KmsInvalidKeyUsage"] = "KMS.InvalidKeyUsageException",
        ["KmsInvalidState"] = "KMS.InvalidStateException",
        ["KmsNotFound"] = "KMS.NotFoundException",
        ["KmsOptInRequired"] = "KMS.OptInRequired",
        ["KmsThrottled"] = "KMS.ThrottlingException",
        ["MessageNotInflight"] = "AWS.SimpleQueueService.MessageNotInflight",
        ["OverLimit"] = "OverLimit",
        ["PurgeQueueInProgress"] = "AWS.SimpleQueueService.PurgeQueueInProgress",
        ["QueueDeletedRecently"] = "AWS.SimpleQueueService.QueueDeletedRecently",
        ["QueueDoesNotExist"] = "AWS.SimpleQueueService.NonExistentQueue",
        ["QueueNameExists"] = "QueueAlreadyExists",
        ["ReceiptHandleIsInvalid"] = "ReceiptHandleIsInvalid",
        ["RequestThrottled"] = "RequestThrottled",
        ["ResourceNotFoundException"] = "ResourceNotFoundException",
        ["TooManyEntriesInBatchRequest"] = "AWS.SimpleQueueService.TooManyEntriesInBatchRequest",
        ["UnsupportedOperation"] = "AWS.SimpleQueueService.UnsupportedOperation",
    };

    /// <summary>The <see cref="HeaderName"/> value for an error with the given JSON code and status.</summary>
    public static string HeaderValue(string errorCode, HttpStatusCode statusCode)
    {
        var queryCode = QueryErrorCodes.GetValueOrDefault(errorCode, errorCode);
        var fault = (int)statusCode >= 500 ? "Receiver" : "Sender";
        return $"{queryCode};{fault}";
    }
}
