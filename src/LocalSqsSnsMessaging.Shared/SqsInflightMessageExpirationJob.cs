namespace LocalSqsSnsMessaging;

internal sealed class SqsInflightMessageExpirationJob : IDisposable
{
    private readonly ITimer _timer;

    private sealed class TimerState
    {
        public SqsQueueResource Queue { get; }
        public string ReceiptHandle { get; }

        public TimeProvider TimeProvider { get; }

        public TimerState(SqsQueueResource queue, string receiptHandle, TimeProvider timeProvider)
        {
            Queue = queue;
            ReceiptHandle = receiptHandle;
            TimeProvider = timeProvider;
        }
    }

    public SqsInflightMessageExpirationJob(string receiptHandle, SqsQueueResource queue, TimeSpan timeout, TimeProvider timeProvider)
    {
        var state = new TimerState(
            queue ?? throw new ArgumentNullException(nameof(queue)),
            receiptHandle ?? throw new ArgumentNullException(nameof(receiptHandle)),
            timeProvider
        );

        _timer = timeProvider.CreateTimer(VisibilityTimeoutCallback, state, timeout, Timeout.InfiniteTimeSpan);
    }

    public void UpdateTimeout(TimeSpan timeout)
    {
        _timer.Change(timeout, Timeout.InfiniteTimeSpan);
    }

    private static void VisibilityTimeoutCallback(object? state)
    {
        var timerState = (TimerState)state!;
        EndVisibilityTimeout(timerState.Queue, timerState.ReceiptHandle, timerState.TimeProvider);
    }

    /// <summary>
    /// Ends the visibility timeout of the message in flight under <paramref name="receiptHandle"/>
    /// now: it becomes visible again, or is discarded if it outlived the queue's retention period.
    /// </summary>
    public static void EndVisibilityTimeout(SqsQueueResource queue, string receiptHandle, TimeProvider timeProvider)
    {
        if (queue.InFlightMessages.TryRemove(receiptHandle, out var inFlightMessage))
        {
            var (message, inFlightExpireCallback) = inFlightMessage;
            if (queue.IsExpired(message, timeProvider.GetUtcNow()))
            {
                // Retention runs from the send, so a message can expire while in flight; it is
                // discarded rather than made visible again.
                if (queue.IsFifo)
                {
                    queue.ReleaseDeletedFifoMessage(message);
                }
            }
            else
            {
                queue.ReturnInFlightMessage(message);
            }

            inFlightExpireCallback.Dispose();
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}
