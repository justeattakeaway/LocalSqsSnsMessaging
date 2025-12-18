using System.ComponentModel;

namespace LocalSqsSnsMessaging;

#if NET
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
            return Task.Delay(delay, timeProvider, cancellationToken);
        }
    }
}
#endif
