using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;

[DebuggerDisplay("{DebuggerDisplay,nq}")]
internal sealed class Utf8XmlWriter : IDisposable, IAsyncDisposable
{
    private const int DefaultGrowthSize = 4096;
    private const int InitialGrowthSize = 256;

    private IBufferWriter<byte>? _output;
#pragma warning disable CA2213
    private Stream? _stream;
#pragma warning restore CA2213
    private ArrayBufferWriter<byte>? _arrayBufferWriter;
    private Memory<byte> _memory;
    private int _bytesPending;
    private int _indentLevel;
    private bool _needsIndent;
    private readonly Stack<string> _elements = new();
    private static readonly SearchValues<char> SearchValues = System.Buffers.SearchValues.Create("<>&\"'");

    private static readonly byte[] s_indentBytes = "  "u8.ToArray();
    private static readonly byte[] s_newLine = "\n"u8.ToArray();
    private static readonly byte[] s_xmlHeader = """<?xml version="1.0" encoding="UTF-8"?>"""u8.ToArray();

    public Utf8XmlWriter(IBufferWriter<byte> output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        WriteHeader();
    }

    public Utf8XmlWriter(Stream utf8Stream)
    {
        ArgumentNullException.ThrowIfNull(utf8Stream);
        if (!utf8Stream.CanWrite) throw new ArgumentException("Stream must be writable", nameof(utf8Stream));

        _stream = utf8Stream;
        _arrayBufferWriter = new ArrayBufferWriter<byte>();
        WriteHeader();
    }

    public long BytesCommitted { get; private set; }
    public int BytesPending => _bytesPending;

    public void Reset()
    {
        CheckNotDisposed();
        _arrayBufferWriter?.Clear();
        ResetHelper();
    }

    public void Reset(Stream utf8Stream)
    {
        CheckNotDisposed();
        ArgumentNullException.ThrowIfNull(utf8Stream);
        if (!utf8Stream.CanWrite) throw new ArgumentException("Stream must be writable", nameof(utf8Stream));

        _stream = utf8Stream;
        if (_arrayBufferWriter == null)
        {
            _arrayBufferWriter = new ArrayBufferWriter<byte>();
        }
        else
        {
            _arrayBufferWriter.Clear();
        }
        _output = null;
        ResetHelper();
    }

    public void Reset(IBufferWriter<byte> output)
    {
        CheckNotDisposed();
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _stream = null;
        _arrayBufferWriter = null;
        ResetHelper();
    }

    private void ResetHelper()
    {
        _bytesPending = 0;
        BytesCommitted = 0;
        _memory = default;
        _indentLevel = 0;
        _needsIndent = false;
        WriteHeader();
    }

    private void CheckNotDisposed()
    {
#pragma warning disable CA1513
        if (_stream == null && _output == null)
        {
            throw new ObjectDisposedException(nameof(Utf8XmlWriter));
        }
#pragma warning restore CA1513
    }

    private void WriteHeader()
    {
        EnsureCapacity(s_xmlHeader.Length + s_newLine.Length);
        WriteBytes(s_xmlHeader);
        WriteNewLine();
    }

    public void WriteStartElement(ReadOnlySpan<char> name)
    {
        WriteIndentIfNeeded();
        
        Span<byte> scratch = stackalloc byte[256];
        int bytesWritten = Encoding.UTF8.GetBytes(name, scratch);
        
        EnsureCapacity(bytesWritten + 2); // < >
        WriteBytes("<"u8);
        WriteBytes(scratch[..bytesWritten]);
        WriteBytes(">"u8);
        WriteNewLine();
        
        _elements.Push(name.ToString());
        _indentLevel++;
    }
    
    public void WriteStartElement(ReadOnlySpan<byte> nameBytes)
    {
        WriteIndentIfNeeded();
        
        EnsureCapacity(nameBytes.Length + 2); // < >
        WriteBytes("<"u8);
        WriteBytes(nameBytes);
        WriteBytes(">"u8);
        WriteNewLine();
        
        _elements.Push(Encoding.UTF8.GetString(nameBytes));
        _indentLevel++;
    }

    public void WriteStartElement(ReadOnlySpan<char> name, ReadOnlySpan<char> ns)
    {
        WriteIndentIfNeeded();
        
        Span<byte> scratch = stackalloc byte[256];
        int bytesWritten = Encoding.UTF8.GetBytes(name, scratch);
        
        Span<byte> nsScratch = stackalloc byte[256];
        int nsBytes = Encoding.UTF8.GetBytes(ns, nsScratch);
        
        EnsureCapacity(bytesWritten + 2); // < >
        WriteBytes("<"u8);
        WriteBytes(scratch[..bytesWritten]);
        WriteBytes(" xmlns=\""u8);
        WriteBytes(nsScratch[..nsBytes]);
        WriteBytes("\">"u8);
        WriteNewLine();
        
        _elements.Push(name.ToString());
        _indentLevel++;
    }
    
    public void WriteStartElement(ReadOnlySpan<byte> nameBytes, ReadOnlySpan<byte> nsBytes)
    {
        WriteIndentIfNeeded();
        
        EnsureCapacity(nameBytes.Length + 2); // < >
        WriteBytes("<"u8);
        WriteBytes(nameBytes);
        WriteBytes(" xmlns=\""u8);
        WriteBytes(nsBytes);
        WriteBytes("\">"u8);
        WriteNewLine();
        
        _elements.Push(Encoding.UTF8.GetString(nameBytes));
        _indentLevel++;
    }

    public void WriteEndElement()
    {
        _indentLevel--;
        var name = _elements.Pop();
        WriteIndentIfNeeded();
        
        Span<byte> scratch = stackalloc byte[256];
        int bytesWritten = Encoding.UTF8.GetBytes(name, scratch);
        
        EnsureCapacity(bytesWritten + 3); // </ >
        WriteBytes("</"u8);
        WriteBytes(scratch[..bytesWritten]);
        WriteBytes(">"u8);
        WriteNewLine();
    }

    public void WriteElementRaw(ReadOnlySpan<byte> nameBytes, ReadOnlySpan<byte> valueBytes)
    {
        WriteIndentIfNeeded();
        
        EnsureCapacity(nameBytes.Length * 2 + valueBytes.Length + 5); // <name>value</name>
        WriteBytes("<"u8);
        WriteBytes(nameBytes);
        WriteBytes(">"u8);
        WriteBytes(valueBytes);
        WriteBytes("</"u8);
        WriteBytes(nameBytes);
        WriteBytes(">"u8);
        WriteNewLine();
    }
    
    public void WriteElement(ReadOnlySpan<byte> nameBytes, ReadOnlySpan<char> value)
    {
        WriteIndentIfNeeded();
        
        // Estimate max escaped value size
        int maxEscapedLength = value.Length * 6; // Worst case: all chars need escaping
        byte[]? valueScratch = maxEscapedLength <= 256 ? null : new byte[maxEscapedLength];
        Span<byte> valueSpan = maxEscapedLength <= 256 ? stackalloc byte[256] : valueScratch;
        
        int valueBytes = WriteEscapedValue(value, valueSpan);

        EnsureCapacity(nameBytes.Length * 2 + valueBytes + 5); // <name>value</name>
        WriteBytes("<"u8);
        WriteBytes(nameBytes);
        WriteBytes(">"u8);
        WriteBytes(valueSpan[..valueBytes]);
        WriteBytes("</"u8);
        WriteBytes(nameBytes);
        WriteBytes(">"u8);
        WriteNewLine();
    }

    public void WriteElement(ReadOnlySpan<char> name, ReadOnlySpan<char> value)
    {
        WriteIndentIfNeeded();
        
        Span<byte> nameScratch = stackalloc byte[256];
        int nameBytes = Encoding.UTF8.GetBytes(name, nameScratch);

        // Estimate max escaped value size
        int maxEscapedLength = value.Length * 6; // Worst case: all chars need escaping
        byte[]? valueScratch = maxEscapedLength <= 256 ? null : new byte[maxEscapedLength];
        Span<byte> valueSpan = maxEscapedLength <= 256 ? stackalloc byte[256] : valueScratch;
        
        int valueBytes = WriteEscapedValue(value, valueSpan);

        EnsureCapacity(nameBytes * 2 + valueBytes + 5); // <name>value</name>
        WriteBytes("<"u8);
        WriteBytes(nameScratch[..nameBytes]);
        WriteBytes(">"u8);
        WriteBytes(valueSpan[..valueBytes]);
        WriteBytes("</"u8);
        WriteBytes(nameScratch[..nameBytes]);
        WriteBytes(">"u8);
        WriteNewLine();
    }

    private static int WriteEscapedValue(ReadOnlySpan<char> value, Span<byte> destination)
    {
        int written = 0;

        while (value.IndexOfAny(SearchValues) is var index && index >= 0)
        {
            if (index > 0)
            {
                written += Encoding.UTF8.GetBytes(value[..index], destination[written..]);
                value = value[index..];
            }

            char c = value[0];
            switch (c)
            {
                case '<':
                    destination[written++] = (byte)'&';
                    destination[written++] = (byte)'l';
                    destination[written++] = (byte)'t';
                    destination[written++] = (byte)';';
                    break;
                case '>':
                    destination[written++] = (byte)'&';
                    destination[written++] = (byte)'g';
                    destination[written++] = (byte)'t';
                    destination[written++] = (byte)';';
                    break;
                case '&':
                    destination[written++] = (byte)'&';
                    destination[written++] = (byte)'a';
                    destination[written++] = (byte)'m';
                    destination[written++] = (byte)'p';
                    destination[written++] = (byte)';';
                    break;
                case '"':
                    destination[written++] = (byte)'&';
                    destination[written++] = (byte)'q';
                    destination[written++] = (byte)'u';
                    destination[written++] = (byte)'o';
                    destination[written++] = (byte)'t';
                    destination[written++] = (byte)';';
                    break;
                case '\'':
                    destination[written++] = (byte)'&';
                    destination[written++] = (byte)'a';
                    destination[written++] = (byte)'p';
                    destination[written++] = (byte)'o';
                    destination[written++] = (byte)'s';
                    destination[written++] = (byte)';';
                    break;
            }
            value = value[1..];
        }
        
        if (value.Length > 0)
        {
            written += Encoding.UTF8.GetBytes(value, destination[written..]);
        }
        
        return written;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteIndentIfNeeded()
    {
        if (_needsIndent)
        {
            int indentSize = _indentLevel * 2;
            EnsureCapacity(indentSize);
            for (int i = 0; i < _indentLevel; i++)
            {
                WriteBytes(s_indentBytes);
            }
            _needsIndent = false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteNewLine()
    {
        WriteBytes(s_newLine);
        _needsIndent = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(_memory.Span[_bytesPending..]);
        _bytesPending += bytes.Length;
    }

    private void EnsureCapacity(int required)
    {
        if (_memory.Length - _bytesPending < required)
        {
            Grow(Math.Max(required, DefaultGrowthSize));
        }
    }

    private void Grow(int sizeHint)
    {
        if (_memory.Length == 0)
        {
            _memory = _output!.GetMemory(Math.Max(InitialGrowthSize, sizeHint));
            return;
        }

        if (_stream != null)
        {
            _arrayBufferWriter!.Advance(_bytesPending);
            _memory = _arrayBufferWriter.GetMemory(sizeHint);
        }
        else
        {
            _output!.Advance(_bytesPending);
            BytesCommitted += _bytesPending;
            _bytesPending = 0;
            _memory = _output.GetMemory(sizeHint);
        }
    }

    public void Flush()
    {
        CheckNotDisposed();
        _memory = default;

        if (_stream != null)
        {
            if (_bytesPending > 0)
            {
                _arrayBufferWriter!.Advance(_bytesPending);
                _stream.Write(_arrayBufferWriter.WrittenSpan);
                BytesCommitted += _arrayBufferWriter.WrittenCount;
                _arrayBufferWriter.Clear();
                _bytesPending = 0;
            }
            _stream.Flush();
        }
        else
        {
            if (_bytesPending > 0)
            {
                _output!.Advance(_bytesPending);
                BytesCommitted += _bytesPending;
                _bytesPending = 0;
            }
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        CheckNotDisposed();
        _memory = default;

        if (_stream != null)
        {
            if (_bytesPending > 0)
            {
                _arrayBufferWriter!.Advance(_bytesPending);
                await _stream.WriteAsync(_arrayBufferWriter.WrittenMemory, cancellationToken).ConfigureAwait(false);
                BytesCommitted += _arrayBufferWriter.WrittenCount;
                _arrayBufferWriter.Clear();
                _bytesPending = 0;
            }
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (_bytesPending > 0)
            {
                _output!.Advance(_bytesPending);
                BytesCommitted += _bytesPending;
                _bytesPending = 0;
            }
        }
    }

    public void Dispose()
    {
        if (_stream == null && _output == null) return;
        
        Flush();
        _stream = null;
        _arrayBufferWriter = null;
        _output = null;
        _memory = default;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream == null && _output == null) return;

        await FlushAsync().ConfigureAwait(false);
        _stream = null;
        _arrayBufferWriter = null;
        _output = null;
        _memory = default;
    }
    
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private string DebuggerDisplay => $"BytesCommitted = {BytesCommitted} BytesPending = {BytesPending}";
}