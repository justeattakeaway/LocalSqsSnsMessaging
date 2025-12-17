// Adapter to allow XmlWriter to write directly to an IBufferWriter
// This avoids intermediate MemoryStream allocations

using System.Buffers;

namespace LocalSqsSnsMessaging.Http.Handlers;

/// <summary>
/// Adapter to allow XmlWriter to write directly to an IBufferWriter.
/// This avoids intermediate MemoryStream allocations.
/// </summary>
internal sealed class BufferWriterStream : Stream
{
    private readonly IBufferWriter<byte> _bufferWriter;
    private long _position;

    public BufferWriterStream(IBufferWriter<byte> bufferWriter)
    {
        _bufferWriter = bufferWriter ?? throw new ArgumentNullException(nameof(bufferWriter));
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        var span = _bufferWriter.GetSpan(count);
        buffer.AsSpan(offset, count).CopyTo(span);
        _bufferWriter.Advance(count);
        _position += count;
    }

#if !NETSTANDARD2_0
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var span = _bufferWriter.GetSpan(buffer.Length);
        buffer.CopyTo(span);
        _bufferWriter.Advance(buffer.Length);
        _position += buffer.Length;
    }
#endif

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer, offset, count);
        await Task.CompletedTask.ConfigureAwait(false);
    }

#if !NETSTANDARD2_0
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        await Task.CompletedTask.ConfigureAwait(false);
    }
#endif

    public override void Flush() { }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
