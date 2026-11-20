using System.Globalization;
using System.Text;
using System.Xml.Linq;
using InMemoryAws.S3;

namespace InMemoryAws.Http;

/// <summary>
/// How S3 answers one request: the status, the headers (response and content headers alike, in the
/// order they were set) and the body, which is either XML the protocol wrote or an object's bytes.
/// The library and the server each copy it onto their own response type.
/// </summary>
internal sealed class S3WireResponse
{
    private readonly List<KeyValuePair<string, string>> _headers = [];
    private S3XmlWriter? _xml;
    private ReadOnlyMemory<byte>? _payload;

    public int Status { get; set; } = 200;

    public IReadOnlyList<KeyValuePair<string, string>> Headers => _headers;

    /// <summary>
    /// True for a HEAD request, or a status S3 sends no body with (204, 304): the headers describe
    /// the body (a HEAD's <c>Content-Length</c> is the object's size), but none is sent.
    /// </summary>
    public bool OmitBody { get; set; }

    /// <summary>An object's bytes, sent as they are.</summary>
    public ReadOnlyMemory<byte>? Payload
    {
        get => _payload;
        set
        {
            _payload = value;
            _xml = null;
        }
    }

    /// <summary>The body to send: the payload, the XML written, or nothing.</summary>
    public ReadOnlyMemory<byte> Body => OmitBody ? ReadOnlyMemory<byte>.Empty : _payload ?? (_xml is null ? ReadOnlyMemory<byte>.Empty : _xml.ToBytes());

    /// <summary>The value of a header set so far, by case-insensitive name.</summary>
    public string? Header(string name)
    {
        foreach (var header in _headers)
        {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return header.Value;
            }
        }

        return null;
    }

    /// <summary>Sets a header, replacing any value it had.</summary>
    public void SetHeader(string name, string value)
    {
        RemoveHeader(name);
        _headers.Add(new KeyValuePair<string, string>(name, value));
    }

    public void RemoveHeader(string name) => _headers.RemoveAll(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Sets a header per entry, each named <paramref name="prefix"/> plus its key (user metadata).</summary>
    public void SetHeaders(string prefix, Dictionary<string, string>? values)
    {
        if (values is null)
        {
            return;
        }

        foreach (var value in values)
        {
            SetHeader(prefix + value.Key, value.Value);
        }
    }

    /// <summary>Starts an XML body (<c>application/xml</c>); the first element written gets S3's namespace.</summary>
    public S3XmlWriter StartXml()
    {
        _payload = null;
        _xml = new S3XmlWriter();
        if (Header("Content-Type") is null)
        {
            SetHeader("Content-Type", "application/xml");
        }

        return _xml;
    }

    /// <summary>A text body (a bucket policy), or none.</summary>
    public void SetText(string? text)
    {
        if (text is not null)
        {
            Payload = Encoding.UTF8.GetBytes(text);
        }
    }
}

/// <summary>
/// Writes S3's XML: a declaration, then elements, the first of them in S3's namespace. S3 escapes the
/// five XML special characters and line breaks, so values survive as they were stored.
/// </summary>
internal sealed class S3XmlWriter
{
    public const string Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    private readonly StringBuilder _text = new("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
    private readonly Stack<string> _open = new();
    private bool _inStartTag;
    private bool _rootWritten;

    /// <summary>When false, the root element is written without S3's namespace (error documents).</summary>
    public bool RootNamespace { get; set; } = true;

    public void StartElement(string name)
    {
        CloseStartTag();
        _text.Append('<').Append(name);
        if (!_rootWritten)
        {
            _rootWritten = true;
            if (RootNamespace)
            {
                _text.Append(" xmlns=\"").Append(Namespace).Append('"');
            }
        }

        _open.Push(name);
        _inStartTag = true;
    }

    public void Attribute(string name, string value)
    {
        if (!_inStartTag)
        {
            throw new InvalidOperationException("Attributes go before an element's content.");
        }

        _text.Append(' ').Append(name).Append("=\"");
        Escape(value, attribute: true);
        _text.Append('"');
    }

    public void Text(string value)
    {
        CloseStartTag();
        Escape(value, attribute: false);
    }

    public void EndElement()
    {
        var name = _open.Pop();
        if (_inStartTag)
        {
            _text.Append("></").Append(name).Append('>');
            _inStartTag = false;
        }
        else
        {
            _text.Append("</").Append(name).Append('>');
        }
    }

    /// <summary>An element with text content: <c>&lt;name&gt;value&lt;/name&gt;</c>.</summary>
    public void Element(string name, string value)
    {
        StartElement(name);
        Text(value);
        EndElement();
    }

    /// <summary>Writes an element S3 keeps as raw XML (a configuration it stores as sent).</summary>
    public void Raw(XElement element)
    {
        CloseStartTag();
        _text.Append(element.ToString(SaveOptions.DisableFormatting));
    }

    public byte[] ToBytes() => Encoding.UTF8.GetBytes(_text.ToString());

    public override string ToString() => _text.ToString();

    private void CloseStartTag()
    {
        if (_inStartTag)
        {
            _text.Append('>');
            _inStartTag = false;
        }
    }

    private void Escape(string value, bool attribute)
    {
        foreach (var c in value)
        {
            switch (c)
            {
                case '&': _text.Append("&amp;"); break;
                case '<': _text.Append("&lt;"); break;
                case '>': _text.Append("&gt;"); break;
                case '"' when attribute: _text.Append("&quot;"); break;
                case '\'' when attribute: _text.Append("&apos;"); break;
                case '\r': _text.Append("&#13;"); break;
                case '\n' when attribute: _text.Append("&#10;"); break;
                case '\t' when attribute: _text.Append("&#9;"); break;
                default:
                    if (c < 0x20 && c is not ('\n' or '\t'))
                    {
                        _text.Append("&#x").Append(((int)c).ToString("X", CultureInfo.InvariantCulture)).Append(';');
                    }
                    else
                    {
                        _text.Append(c);
                    }

                    break;
            }
        }
    }
}
