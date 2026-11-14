namespace LocalSqsSnsMessaging;

internal sealed class SqsMoveTask
{
    public required string TaskHandle { get; init; }
    public required SqsQueueResource SourceQueue { get; init; }
    public SqsQueueResource? DestinationQueue { get; init; }
    public int? MaxNumberOfMessagesPerSecond { get; set; }
    public int ApproximateNumberOfMessagesMoved { get; set; }
    public int ApproximateNumberOfMessagesToMove { get; set; }
    public required SqsMoveTaskJob MoveTaskJob { get; init; }
    public required string Status { get; set; }
    public string? FailureReason { get; set; }
    public required long StartedTimestamp { get; init; }

    private static long _nextSequence;

    /// <summary>Start order, to list tasks most recent first even when they start in the same millisecond.</summary>
    public long Sequence { get; } = Interlocked.Increment(ref _nextSequence);
}
