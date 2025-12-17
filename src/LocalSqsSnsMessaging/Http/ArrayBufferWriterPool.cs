// Pooled ArrayBufferWriter to reduce allocations for HTTP response serialization

using System.Buffers;
#if NETSTANDARD2_0
using LocalSqsSnsMessaging.Http.Internal;
#endif

namespace LocalSqsSnsMessaging.Http;

/// <summary>
/// Pool for ArrayBufferWriter&lt;byte&gt; instances to reduce allocations.
/// Uses a simple per-thread cache for zero-contention pooling.
/// </summary>
internal static class ArrayBufferWriterPool
{
    private const int DefaultInitialCapacity = 4096; // 4KB initial size
    private const int MaxRetainedCapacity = 1024 * 1024; // 1MB max to prevent memory bloat

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? t_cachedInstance;

    /// <summary>
    /// Rent an ArrayBufferWriter from the pool.
    /// The buffer is cleared and ready to use.
    /// </summary>
    public static ArrayBufferWriter<byte> Rent()
    {
        var buffer = t_cachedInstance;
        if (buffer != null)
        {
            t_cachedInstance = null;
            buffer.Clear();
            return buffer;
        }

        return new ArrayBufferWriter<byte>(DefaultInitialCapacity);
    }

    /// <summary>
    /// Return an ArrayBufferWriter to the pool.
    /// If the buffer is too large, it will be discarded to prevent memory bloat.
    /// </summary>
    public static void Return(ArrayBufferWriter<byte> buffer)
    {
        if (buffer == null)
            return;

        // Don't retain buffers that grew too large
        if (buffer.Capacity > MaxRetainedCapacity)
            return;

        // Only cache one per thread (simple and zero-contention)
        if (t_cachedInstance == null)
        {
            t_cachedInstance = buffer;
        }
    }
}

/// <summary>
/// RAII wrapper for pooled ArrayBufferWriter to ensure it's returned to the pool.
/// Use with 'using' statement for automatic cleanup.
/// </summary>
internal readonly struct PooledArrayBufferWriter : IDisposable
{
    private readonly ArrayBufferWriter<byte> _buffer;

    private PooledArrayBufferWriter(ArrayBufferWriter<byte> buffer)
    {
        _buffer = buffer;
    }

    /// <summary>
    /// Get a pooled ArrayBufferWriter.
    /// Must be disposed to return to pool.
    /// </summary>
    public static PooledArrayBufferWriter Rent() => new(ArrayBufferWriterPool.Rent());

    /// <summary>
    /// The underlying ArrayBufferWriter.
    /// </summary>
    public ArrayBufferWriter<byte> Writer => _buffer;

    /// <summary>
    /// Return the buffer to the pool.
    /// </summary>
    public void Dispose()
    {
        ArrayBufferWriterPool.Return(_buffer);
    }
}
