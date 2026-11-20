namespace InMemoryAws.Http;

/// <summary>
/// Represents the AWS service type for routing requests to the in-memory cloud.
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
    EventBridge,

    /// <summary>
    /// Amazon DynamoDB
    /// </summary>
    DynamoDb,

    /// <summary>
    /// Amazon DynamoDB Streams
    /// </summary>
    DynamoDbStreams,

    /// <summary>
    /// Amazon Simple Storage Service (S3)
    /// </summary>
    S3
}
