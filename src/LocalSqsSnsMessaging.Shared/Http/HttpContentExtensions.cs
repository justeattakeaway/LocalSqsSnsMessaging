using System.ComponentModel;

namespace LocalSqsSnsMessaging.Http;

#if NETSTANDARD2_0
[EditorBrowsable(EditorBrowsableState.Never)]
internal static class HttpContentExtensions
{
    extension(HttpContent content)
    {
        public Task<byte[]> ReadAsByteArrayAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return  content.ReadAsByteArrayAsync();
        }
    }
}
#endif
