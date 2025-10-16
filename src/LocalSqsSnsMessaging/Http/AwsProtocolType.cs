namespace LocalSqsSnsMessaging.Http;

/// <summary>
/// Represents the AWS service protocol type.
/// </summary>
internal enum AwsProtocolType
{
    /// <summary>
    /// JSON protocol with x-amz-target header (used by SQS, DynamoDB, etc.)
    /// </summary>
    Json,

    /// <summary>
    /// Query protocol with form-urlencoded requests and XML responses (used by SNS, EC2, etc.)
    /// </summary>
    Query
}
