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
        if (timerState.Queue.InFlightMessages.TryRemove(timerState.ReceiptHandle, out var inFlightMessage))
        {
            var (message, inFlightExpireCallback) = inFlightMessage;
            if (timerState.Queue.IsExpired(message, timerState.TimeProvider.GetUtcNow()))
            {
                // Retention runs from the send, so a message can expire while in flight; it is
                // discarded rather than made visible again.
                if (timerState.Queue.IsFifo)
                {
                    timerState.Queue.ReleaseDeletedFifoMessage(message);
                }
            }
            else
            {
                timerState.Queue.ReturnInFlightMessage(message);
            }

            inFlightExpireCallback.Dispose();
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}
