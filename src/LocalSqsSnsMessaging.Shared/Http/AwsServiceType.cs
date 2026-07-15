namespace LocalSqsSnsMessaging.Http;

/// <summary>
/// Represents the AWS service type for routing requests to the in-memory bus.
/// </summary>
public enum AwsServiceType
{
    /// <summary>
    /// Amazon Simple Queue Service (SQS)
    /// </summary>
    Sqs,

    /// <summary>
    /// Amazon Simple Notification Service (SNS)
    /// </summary>
    Sns,

    /// <summary>
    /// Amazon EventBridge
    /// </summary>
    EventBridge
}
