namespace LocalSqsSnsMessaging;

internal sealed class SqsInflightMessageExpirationJob : IDisposable
{
    private readonly ITimer _timer;

    private sealed class TimerState
    {
        public SqsQueueResource Queue { get; }
        public string ReceiptHandle { get; }

        public TimerState(SqsQueueResource queue, string receiptHandle)
        {
            Queue = queue;
            ReceiptHandle = receiptHandle;
        }
    }

    public SqsInflightMessageExpirationJob(string receiptHandle, SqsQueueResource queue, TimeSpan timeout, TimeProvider timeProvider)
    {
        var state = new TimerState(
            queue ?? throw new ArgumentNullException(nameof(queue)),
            receiptHandle ?? throw new ArgumentNullException(nameof(receiptHandle))
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
            timerState.Queue.Messages.Writer.TryWrite(message);
            inFlightExpireCallback.Dispose();
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}
