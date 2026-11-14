using LocalSqsSnsMessaging.Sqs.Model;

namespace LocalSqsSnsMessaging;

internal sealed class SqsMoveTaskJob : IDisposable
{
    private readonly ITimer _timer;

    public SqsMoveTaskJob(TimeProvider timeProvider, SqsQueueResource sourceQueue, SqsQueueResource? destinationQueue, InMemoryAwsBus bus, int? rateLimitPerSecond, string taskHandle)
    {
        _timer = timeProvider.CreateTimer(MoveMessages, (sourceQueue, destinationQueue, bus, rateLimitPerSecond, taskHandle), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private static void MoveMessages(object? state)
    {
        var (sourceQueue, destinationQueue, bus, rateLimitPerSecond, taskHandle) = ((SqsQueueResource, SqsQueueResource?, InMemoryAwsBus, int?, string))state!;
        if (!bus.MoveTasks.TryGetValue(taskHandle, out var moveTask) || moveTask.Status != MoveTaskStatus.Running)
        {
            return;
        }

        var messagesMovedThisIteration = 0;
        string? failureReason = null;

        // Check the rate limit before taking a message, so none is dequeued and then left behind.
        while ((rateLimitPerSecond is null || messagesMovedThisIteration < rateLimitPerSecond)
               && sourceQueue.Messages.TryDequeue(out var message))
        {
            var targetQueue = destinationQueue;
            if (targetQueue is null
                && !(message.Attributes?.TryGetValue(InternalMessageSystemAttributeName.DeadLetterQueueSourceArn, out var deadLetterSourceArn) == true
                     && bus.TryGetQueueByArn(deadLetterSourceArn, out targetQueue)))
            {
                // A message that was not dead-lettered from a queue (sent to the DLQ directly, or
                // its source deleted) has nowhere to go back to: SQS fails the task and leaves the
                // message where it is.
                sourceQueue.Messages.Enqueue(message);
                failureReason = "CouldNotDetermineMessageSource";
                break;
            }

            var newMessage = CloneNewMessage(message, bus.TimeProvider);
            if (targetQueue!.IsFifo && newMessage.Attributes?.TryGetValue("MessageGroupId", out var groupId) == true && !string.IsNullOrEmpty(groupId))
            {
                targetQueue.EnqueueFifoMessage(groupId, newMessage);
            }
            else
            {
                targetQueue.Messages.Enqueue(newMessage);
            }

            messagesMovedThisIteration++;
        }

        moveTask.ApproximateNumberOfMessagesMoved += messagesMovedThisIteration;

        if (failureReason is not null)
        {
            moveTask.Status = MoveTaskStatus.Failed;
            moveTask.FailureReason = failureReason;
            moveTask.MoveTaskJob.Dispose();
        }
        else if (moveTask.ApproximateNumberOfMessagesMoved >= moveTask.ApproximateNumberOfMessagesToMove
                 || (sourceQueue.Messages.Count == 0 && sourceQueue.InFlightMessages.IsEmpty))
        {
            // ApproximateNumberOfMessagesToMove stays at the count the task started with, as in SQS.
            moveTask.Status = MoveTaskStatus.Completed;
            moveTask.MoveTaskJob.Dispose();
        }
    }

    /// <summary>
    /// The copy a move task delivers: SQS gives a redriven message a new MessageId and SentTimestamp
    /// and restarts its receive count.
    /// </summary>
    private static Message CloneNewMessage(Message source, TimeProvider timeProvider)
    {
        var newMessage = new Message
        {
            MessageId = Guid.NewGuid().ToString(),
            Body = source.Body,
            MD5OfBody = source.MD5OfBody,
            Attributes = source.Attributes.ToInitializedDictionary(),
            MessageAttributes = source.MessageAttributes.ToInitializedDictionary(),
            MD5OfMessageAttributes = source.MD5OfMessageAttributes
        };

        newMessage.Attributes?.Remove(InternalMessageSystemAttributeName.ApproximateFirstReceiveTimestamp);
        newMessage.Attributes?.Remove(InternalMessageSystemAttributeName.ApproximateReceiveCount);
        newMessage.Attributes?.Remove("SequenceNumber");
        newMessage.Attributes ??= [];
        newMessage.Attributes[InternalMessageSystemAttributeName.SentTimestamp] =
            timeProvider.GetUtcNow().ToUnixTimeMilliseconds().ToString(NumberFormatInfo.InvariantInfo);
        return newMessage;
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}

internal static class MoveTaskStatus
{
    public const string Running = "RUNNING";
    public const string Completed = "COMPLETED";
    public const string Cancelling = "CANCELLING";
    public const string Cancelled = "CANCELLED";
    public const string Failed = "FAILED";
}
