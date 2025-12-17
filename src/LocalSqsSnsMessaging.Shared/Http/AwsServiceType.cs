namespace LocalSqsSnsMessaging.Http;

/// <summary>
/// Represents the AWS service type for routing requests.
/// </summary>
internal enum AwsServiceType
{
    /// <summary>
    /// Amazon Simple Queue Service (SQS)
    /// </summary>
    Sqs,

    /// <summary>
    /// Amazon Simple Notification Service (SNS)
    /// </summary>
    Sns
}
