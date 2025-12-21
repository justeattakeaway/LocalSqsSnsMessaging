namespace LocalSqsSnsMessaging;

/// <summary>
/// Constants for AWS service names used in API usage tracking.
/// </summary>
public static class AwsServiceName
{
    /// <summary>Amazon Simple Queue Service.</summary>
    public const string Sqs = "sqs";

    /// <summary>Amazon Simple Notification Service.</summary>
    public const string Sns = "sns";
}

/// <summary>
/// Constants for SQS action names used in API usage tracking.
/// </summary>
public static class SqsActionName
{
    /// <summary>Grants permission to add a permission to a queue for a specific principal.</summary>
    public const string AddPermission = "AddPermission";

    /// <summary>Grants permission to cancel a message move task.</summary>
    public const string CancelMessageMoveTask = "CancelMessageMoveTask";

    /// <summary>Grants permission to change the visibility timeout of a message.</summary>
    public const string ChangeMessageVisibility = "ChangeMessageVisibility";

    /// <summary>Grants permission to change the visibility timeout of multiple messages.</summary>
    public const string ChangeMessageVisibilityBatch = "ChangeMessageVisibilityBatch";

    /// <summary>Grants permission to create a new queue.</summary>
    public const string CreateQueue = "CreateQueue";

    /// <summary>Grants permission to delete a message from a queue.</summary>
    public const string DeleteMessage = "DeleteMessage";

    /// <summary>Grants permission to delete multiple messages from a queue.</summary>
    public const string DeleteMessageBatch = "DeleteMessageBatch";

    /// <summary>Grants permission to delete a queue.</summary>
    public const string DeleteQueue = "DeleteQueue";

    /// <summary>Grants permission to get attributes for a queue.</summary>
    public const string GetQueueAttributes = "GetQueueAttributes";

    /// <summary>Grants permission to get the URL for a queue.</summary>
    public const string GetQueueUrl = "GetQueueUrl";

    /// <summary>Grants permission to list dead-letter source queues.</summary>
    public const string ListDeadLetterSourceQueues = "ListDeadLetterSourceQueues";

    /// <summary>Grants permission to list message move tasks.</summary>
    public const string ListMessageMoveTasks = "ListMessageMoveTasks";

    /// <summary>Grants permission to list queues.</summary>
    public const string ListQueues = "ListQueues";

    /// <summary>Grants permission to list queue tags.</summary>
    public const string ListQueueTags = "ListQueueTags";

    /// <summary>Grants permission to purge all messages from a queue.</summary>
    public const string PurgeQueue = "PurgeQueue";

    /// <summary>Grants permission to receive messages from a queue.</summary>
    public const string ReceiveMessage = "ReceiveMessage";

    /// <summary>Grants permission to remove a permission from a queue.</summary>
    public const string RemovePermission = "RemovePermission";

    /// <summary>Grants permission to send a message to a queue.</summary>
    public const string SendMessage = "SendMessage";

    /// <summary>Grants permission to send multiple messages to a queue.</summary>
    public const string SendMessageBatch = "SendMessageBatch";

    /// <summary>Grants permission to set attributes for a queue.</summary>
    public const string SetQueueAttributes = "SetQueueAttributes";

    /// <summary>Grants permission to start a message move task.</summary>
    public const string StartMessageMoveTask = "StartMessageMoveTask";

    /// <summary>Grants permission to add tags to a queue.</summary>
    public const string TagQueue = "TagQueue";

    /// <summary>Grants permission to remove tags from a queue.</summary>
    public const string UntagQueue = "UntagQueue";
}

/// <summary>
/// Constants for SNS action names used in API usage tracking.
/// </summary>
public static class SnsActionName
{
    /// <summary>Grants permission to add a permission to a topic.</summary>
    public const string AddPermission = "AddPermission";

    /// <summary>Grants permission to create a topic.</summary>
    public const string CreateTopic = "CreateTopic";

    /// <summary>Grants permission to delete a topic.</summary>
    public const string DeleteTopic = "DeleteTopic";

    /// <summary>Grants permission to get subscription attributes.</summary>
    public const string GetSubscriptionAttributes = "GetSubscriptionAttributes";

    /// <summary>Grants permission to get topic attributes.</summary>
    public const string GetTopicAttributes = "GetTopicAttributes";

    /// <summary>Grants permission to list subscriptions.</summary>
    public const string ListSubscriptions = "ListSubscriptions";

    /// <summary>Grants permission to list subscriptions by topic.</summary>
    public const string ListSubscriptionsByTopic = "ListSubscriptionsByTopic";

    /// <summary>Grants permission to list tags for a resource.</summary>
    public const string ListTagsForResource = "ListTagsForResource";

    /// <summary>Grants permission to list topics.</summary>
    public const string ListTopics = "ListTopics";

    /// <summary>Grants permission to publish a message to a topic.</summary>
    public const string Publish = "Publish";

    /// <summary>Grants permission to publish multiple messages to a topic.</summary>
    public const string PublishBatch = "PublishBatch";

    /// <summary>Grants permission to remove a permission from a topic.</summary>
    public const string RemovePermission = "RemovePermission";

    /// <summary>Grants permission to set subscription attributes.</summary>
    public const string SetSubscriptionAttributes = "SetSubscriptionAttributes";

    /// <summary>Grants permission to set topic attributes.</summary>
    public const string SetTopicAttributes = "SetTopicAttributes";

    /// <summary>Grants permission to subscribe to a topic.</summary>
    public const string Subscribe = "Subscribe";

    /// <summary>Grants permission to add tags to a resource.</summary>
    public const string TagResource = "TagResource";

    /// <summary>Grants permission to unsubscribe from a topic.</summary>
    public const string Unsubscribe = "Unsubscribe";

    /// <summary>Grants permission to remove tags from a resource.</summary>
    public const string UntagResource = "UntagResource";
}
