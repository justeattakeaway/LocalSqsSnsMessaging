namespace LocalSqsSnsMessaging;

/// <summary>
/// String constants for SQS queue attribute names.
/// Mirrors Amazon.SQS.Model.QueueAttributeName values.
/// </summary>
internal static class QueueAttributeName
{
    public const string ApproximateNumberOfMessages = "ApproximateNumberOfMessages";
    public const string ApproximateNumberOfMessagesDelayed = "ApproximateNumberOfMessagesDelayed";
    public const string ApproximateNumberOfMessagesNotVisible = "ApproximateNumberOfMessagesNotVisible";
    public const string CreatedTimestamp = "CreatedTimestamp";
    public const string LastModifiedTimestamp = "LastModifiedTimestamp";
    public const string QueueArn = "QueueArn";
    public const string VisibilityTimeout = "VisibilityTimeout";
    public const string RedrivePolicy = "RedrivePolicy";
    public const string DeduplicationScope = "DeduplicationScope";
    public const string FifoThroughputLimit = "FifoThroughputLimit";
}

/// <summary>
/// String constants for SQS message system attribute names.
/// Mirrors Amazon.SQS.Model.MessageSystemAttributeName values.
/// </summary>
internal static class MessageSystemAttributeName
{
    public const string ApproximateReceiveCount = "ApproximateReceiveCount";
    public const string ApproximateFirstReceiveTimestamp = "ApproximateFirstReceiveTimestamp";
    public const string SentTimestamp = "SentTimestamp";
    public const string DeadLetterQueueSourceArn = "DeadLetterQueueSourceArn";
    public const string MessageDeduplicationId = "MessageDeduplicationId";
}
