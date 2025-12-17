using System.ComponentModel;

namespace LocalSqsSnsMessaging;

#if !NETSTANDARD2_0
[EditorBrowsable(EditorBrowsableState.Never)]
internal static class TimeProviderExtensions
{
    extension(TimeProvider timeProvider)
    {
        public CancellationTokenSource CreateCancellationTokenSource(TimeSpan delay)
        {
            return new CancellationTokenSource(delay, timeProvider);
        }

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            if (timeProvider == TimeProvider.System)
            {
                return Task.Delay(delay, cancellationToken);
            }

            ArgumentNullException.ThrowIfNull(timeProvider);

            if (delay != Timeout.InfiniteTimeSpan && delay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(delay));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            if (delay == TimeSpan.Zero)
            {
                return Task.CompletedTask;
            }

            return Task.Delay(delay, timeProvider, cancellationToken);
        }
    }
}
#endif
