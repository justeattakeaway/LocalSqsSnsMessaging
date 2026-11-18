#if !ASPNETCORE
using System.Buffers;

namespace InMemoryAws.Http;

/// <summary>
/// The body of a request the library intercepts, copied once into a buffer rented from
/// <see cref="ArrayPool{T}"/>, which <see cref="Dispose"/> returns.
/// </summary>
internal readonly struct PooledRequestBody : IDisposable
{
    private const int UnknownLengthCapacity = 4096;

    private readonly byte[]? _buffer;
    private readonly int _length;

    private PooledRequestBody(byte[] buffer, int length)
    {
        _buffer = buffer;
        _length = length;
    }

    public ReadOnlyMemory<byte> Memory => _buffer is null ? ReadOnlyMemory<byte>.Empty : _buffer.AsMemory(0, _length);

    public static async ValueTask<PooledRequestBody> ReadAsync(HttpContent? content, CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return default;
        }

        var declared = content.Headers.ContentLength;
#if NET
        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
        using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
        // Rent room for one byte more than declared, so the read that finds the end needs no new buffer.
        var buffer = ArrayPool<byte>.Shared.Rent(declared is >= 0 and < int.MaxValue ? (int)declared.Value + 1 : UnknownLengthCapacity);
        var length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    var larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    Buffer.BlockCopy(buffer, 0, larger, 0, length);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }

#if NET
                var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
#else
                var read = await stream.ReadAsync(buffer, length, buffer.Length - length, cancellationToken).ConfigureAwait(false);
#endif
                if (read == 0)
                {
                    return new PooledRequestBody(buffer, length);
                }

                length += read;
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    public void Dispose()
    {
        if (_buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
        }
    }
}
#endif
