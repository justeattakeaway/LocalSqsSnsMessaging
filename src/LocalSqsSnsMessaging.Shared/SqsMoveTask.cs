using LocalSqsSnsMessaging.Sqs.Model;

namespace LocalSqsSnsMessaging;

/// <summary>
/// A message move task (<c>StartMessageMoveTask</c>): once a second, on the bus's
/// <see cref="TimeProvider"/>, it moves a dead-letter queue's visible messages to a destination
/// queue, or each back to the queue it was dead-lettered from, as redriven messages.
/// </summary>
/// <remarks>
/// The task keeps its queues' ARNs and resolves them on every tick (ARN resolution): a destination
/// deleted and recreated under the same ARN keeps receiving. A tick and a cancel exclude each other,
/// so the task settles on exactly one of COMPLETED, FAILED or CANCELLED.
/// </remarks>
internal sealed class SqsMoveTask
{
    public required string TaskHandle { get; init; }
    public required string SourceArn { get; init; }

    /// <summary>Where every message goes; <see langword="null"/> to send each back to its dead-letter source.</summary>
    public string? DestinationArn { get; init; }

    public int? MaxNumberOfMessagesPerSecond { get; init; }

    /// <summary>The count the source held when the task started (visible and in flight), as SQS reports it throughout.</summary>
    public required int ApproximateNumberOfMessagesToMove { get; init; }

    public required long StartedTimestamp { get; init; }

    public int ApproximateNumberOfMessagesMoved { get; private set; }
    public string Status { get; private set; } = MoveTaskStatus.Running;
    public string? FailureReason { get; private set; }

    private static long _nextSequence;

    /// <summary>Start order, to list tasks most recent first even when they start in the same millisecond.</summary>
    public long Sequence { get; } = Interlocked.Increment(ref _nextSequence);

    /// <summary>Guards the task's progress and status; held for a whole tick.</summary>
    private readonly object _lock = new();

    private ITimer? _timer;

    /// <summary>Starts moving, a second from now and every second after.</summary>
    public void Start(InMemoryAwsBus bus)
    {
        lock (_lock)
        {
            _timer = bus.TimeProvider.CreateTimer(_ => Tick(bus), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>Cancels the task if it is still running.</summary>
    /// <returns><see langword="false"/> when the task had already completed, failed or been cancelled.</returns>
    public bool TryCancel()
    {
        lock (_lock)
        {
            if (Status != MoveTaskStatus.Running)
            {
                return false;
            }

            Finish(MoveTaskStatus.Cancelled);
            return true;
        }
    }

    private void Tick(InMemoryAwsBus bus)
    {
        lock (_lock)
        {
            if (Status != MoveTaskStatus.Running)
            {
                return;
            }

            if (!bus.TryGetQueueByArn(SourceArn, out var source))
            {
                // The dead-letter queue was deleted: nothing is left to move.
                Finish(MoveTaskStatus.Completed);
                return;
            }

            SqsQueueResource? destination = null;
            if (DestinationArn is not null && !bus.TryGetQueueByArn(DestinationArn, out destination))
            {
                // The destination was deleted: the messages wait for a queue of that ARN.
                return;
            }

            string? failureReason = null;
            var now = bus.TimeProvider.GetUtcNow();
            ApproximateNumberOfMessagesMoved += source.MoveMessages(MaxNumberOfMessagesPerSecond, message =>
            {
                var target = destination;
                if (target is null
                    && !(message.Attributes?.TryGetValue(InternalMessageSystemAttributeName.DeadLetterQueueSourceArn, out var deadLetterSourceArn) == true
                         && bus.TryGetQueueByArn(deadLetterSourceArn, out target)))
                {
                    // A message that was not dead-lettered from a queue (sent to the DLQ directly, or
                    // its source deleted) has nowhere to go back to: SQS fails the task and leaves the
                    // message where it is.
                    failureReason = "CouldNotDetermineMessageSource";
                    return false;
                }

                target!.AcceptRedriven(CloneNewMessage(message, now), now);
                return true;
            });

            if (failureReason is not null)
            {
                FailureReason = failureReason;
                Finish(MoveTaskStatus.Failed);
            }
            else if (ApproximateNumberOfMessagesMoved >= ApproximateNumberOfMessagesToMove
                     || source.CountMessages() is { Visible: 0, InFlight: 0 })
            {
                Finish(MoveTaskStatus.Completed);
            }
        }
    }

    /// <summary>Settles the task. The caller holds <see cref="_lock"/>.</summary>
    private void Finish(string status)
    {
        Status = status;
        _timer?.Dispose();
    }

    /// <summary>
    /// The copy a move task delivers: SQS gives a redriven message a new MessageId and SentTimestamp
    /// and restarts its receive count.
    /// </summary>
    private static Message CloneNewMessage(Message source, DateTimeOffset now)
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
            now.ToUnixTimeMilliseconds().ToString(NumberFormatInfo.InvariantInfo);
        return newMessage;
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
