using Amazon.SQS;
using Amazon.SQS.Model;

namespace LocalSqsSnsMessaging;

internal sealed class SqsMoveTaskJob : IDisposable
{
    private readonly ITimer _timer;

    public SqsMoveTaskJob(TimeProvider timeProvider, SqsQueueResource sourceQueue, SqsQueueResource? destinationQueue, InMemoryAwsBus bus, int? rateLimitPerSecond)
    {
        _timer = timeProvider.CreateTimer(MoveMessages, (sourceQueue, destinationQueue, bus, rateLimitPerSecond), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private static void MoveMessages(object? state)
    {
        var (sourceQueue, destinationQueue, bus, rateLimitPerSecond) = ((SqsQueueResource, SqsQueueResource?, InMemoryAwsBus, int?))state!;
        var messagesMoveThisIteration = 0;
        while (sourceQueue.Messages.Reader.TryRead(out var message) && messagesMoveThisIteration < rateLimitPerSecond)
        {
            var newMessage = CloneNewMessage(message);
            if (destinationQueue is not null)
            {
                destinationQueue.Messages.Writer.TryWrite(newMessage);
            }
            else
            {
                if (message.Attributes?.TryGetValue(MessageSystemAttributeName.DeadLetterQueueSourceArn, out var deadLetterSourceArn) == true)
                {
                    var deadLetterSourceQueueName = GetQueueNameFromArn(deadLetterSourceArn);
                    if (!bus.Queues.TryGetValue(deadLetterSourceQueueName, out var deadLetterSourceQueue))
                    {
                        continue;
                    }
                    deadLetterSourceQueue.Messages.Writer.TryWrite(newMessage);
                }
            }
            messagesMoveThisIteration++;
        }

        if (bus.MoveTasks.TryGetValue(sourceQueue.Name, out var moveTask))
        {
            moveTask.ApproximateNumberOfMessagesMoved += messagesMoveThisIteration;
            moveTask.ApproximateNumberOfMessagesToMove -= messagesMoveThisIteration;

            if (moveTask.ApproximateNumberOfMessagesToMove >= 0)
            {
                moveTask.Status = MoveTaskStatus.Completed;
                moveTask.MoveTaskJob.Dispose();
            }
        }
    }

    private static Message CloneNewMessage(Message source)
    {
        var newMessage = new Message
        {
            MessageId = source.MessageId,
            Body = source.Body,
            MD5OfBody = source.MD5OfBody,
            Attributes = source.Attributes.ToInitializedDictionary(),
            MessageAttributes = source.MessageAttributes.ToInitializedDictionary(),
            MD5OfMessageAttributes = source.MD5OfMessageAttributes
        };

        newMessage.Attributes?.Remove(MessageSystemAttributeName.ApproximateFirstReceiveTimestamp);
        newMessage.Attributes?.Remove(MessageSystemAttributeName.ApproximateReceiveCount);
        newMessage.Attributes?.Remove(MessageSystemAttributeName.SentTimestamp);
        return newMessage;
    }

    private static string GetQueueNameFromArn(string queueArn)
    {
        var indexOfLastColon = queueArn.LastIndexOf(':');
        if (indexOfLastColon == -1)
        {
            throw new ArgumentException("ARN malformed", nameof(queueArn));
        }
        return queueArn[(indexOfLastColon+1) ..];
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
