namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite;

/// <summary>
/// Waits and measurements on the running check's virtual clock, in place of <c>Task.Delay</c>,
/// <c>Stopwatch</c> and <c>DateTimeOffset.UtcNow</c>. Each reads the <see cref="FidelityContext"/>
/// created on the current async flow, so helpers can use it without being handed the context.
/// </summary>
public static class FidelityTime
{
    /// <summary>A stopwatch on virtual time, started now: <c>Stopwatch.StartNew()</c>.</summary>
    public static FidelityStopwatch StartNew() => new(FidelityContext.Current.Time);

    /// <summary>Waits <paramref name="delay"/> of virtual time: <c>Task.Delay(delay)</c>.</summary>
    public static Task Delay(TimeSpan delay, CancellationToken cancellationToken = default) =>
        FidelityContext.Current.Delay(delay, cancellationToken);

    /// <summary>The virtual wall clock: <c>DateTimeOffset.UtcNow</c>.</summary>
    public static DateTimeOffset UtcNow => FidelityContext.Current.Time.GetUtcNow();
}

/// <summary>Elapsed virtual time since it was created: the part of <c>Stopwatch</c> the checks use.</summary>
public sealed class FidelityStopwatch(TimeProvider time)
{
    private readonly long _start = time.GetTimestamp();

    public TimeSpan Elapsed => time.GetElapsedTime(_start);
}
