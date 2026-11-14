using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalSqsSnsMessaging;

internal static partial class SnsFilterPolicy
{
    /// <summary>
    /// Reads filter policy text as SNS's JSON parser does, so a document that isn't JSON is refused
    /// with the parser's own reason and line/column (SNS uses Jackson, and redacts the source text
    /// from the location). Duplicate keys keep their first position and take the last value.
    /// </summary>
    private sealed class PolicyTextReader
    {
        private const string ValidValues = "(JSON String, Number, Array, Object or token 'null', 'true' or 'false')";
        private const string RedactedSource = "REDACTED (`StreamReadFeature.INCLUDE_SOURCE_IN_LOCATION` disabled)";

        // Jackson reports at most this many characters of an unrecognized token.
        private const int MaxReportedTokenLength = 256;

        /// <summary>
        /// The deepest nesting of objects and arrays the reader accepts, Jackson's default. The reader
        /// recurses per level, so without a limit a policy of nested brackets could exhaust the stack.
        /// </summary>
        private const int MaxNestingDepth = 1000;

        private int _depth;

        private readonly string _text;
        private int _position;
        private int _line = 1;
        private int _lineStart;

        private PolicyTextReader(string text) => _text = text;

        /// <summary>
        /// Parses <paramref name="text"/> into a JSON node (<see langword="null"/> for a JSON <c>null</c>),
        /// throwing <see cref="InternalInvalidParameterException"/> with SNS's "failed to parse JSON"
        /// message when it is not a single JSON value.
        /// </summary>
        public static JsonNode? Read(string text)
        {
            var reader = new PolicyTextReader(text);
            reader.SkipWhitespace();
            var root = reader.ReadValue();
            reader.SkipWhitespace();
            if (!reader.AtEnd)
            {
                throw reader.UnexpectedCharacter("expected end-of-input");
            }

            return root;
        }

        private bool AtEnd => _position >= _text.Length;

        private char Peek => _text[_position];

        private void EnterNesting(int line, int column)
        {
            if (++_depth > MaxNestingDepth)
            {
                throw Error(
                    $"Document nesting depth ({_depth}) exceeds the maximum allowed ({MaxNestingDepth}, from `StreamReadConstraints.getMaxNestingDepth()`)",
                    line, column);
            }
        }

        private JsonNode? ReadValue()
        {
            if (AtEnd)
            {
                throw Error("Unexpected end-of-input: expected a valid value " + ValidValues, _line, Column(_position));
            }

            var c = Peek;
            switch (c)
            {
                case '{':
                    return ReadObject();
                case '[':
                    return ReadArray();
                case '"':
                    return JsonValue.Create(ReadString());
                case '-':
                case >= '0' and <= '9':
                    return ReadNumber();
            }

            if (char.IsLetter(c) || c is '_' or '$')
            {
                return ReadLiteral();
            }

            throw UnexpectedCharacter("expected a valid value " + ValidValues);
        }

        private JsonObject ReadObject()
        {
            EnterNesting(_line, Column(_position));
            try
            {
                return ReadObjectBody();
            }
            finally
            {
                _depth--;
            }
        }

        private JsonObject ReadObjectBody()
        {
            var (startLine, startColumn) = (_line, Column(_position));
            _position++;
            var result = new JsonObject();

            SkipWhitespaceInside("Object", startLine, startColumn);
            if (Peek == '}')
            {
                _position++;
                return result;
            }

            while (true)
            {
                if (Peek == ']')
                {
                    throw MismatchedCloseMarker(']', "Object", startLine, startColumn);
                }
                if (Peek != '"')
                {
                    throw UnexpectedCharacter("was expecting double-quote to start field name");
                }

                var name = ReadString();
                SkipWhitespaceInside("Object", startLine, startColumn);
                if (Peek != ':')
                {
                    throw UnexpectedCharacter("was expecting a colon to separate field name and value");
                }

                _position++;
                SkipWhitespaceInside("Object", startLine, startColumn);
                result[name] = ReadValue();

                SkipWhitespaceInside("Object", startLine, startColumn);
                switch (Peek)
                {
                    case '}':
                        _position++;
                        return result;
                    case ']':
                        throw MismatchedCloseMarker(']', "Object", startLine, startColumn);
                    case ',':
                        _position++;
                        SkipWhitespaceInside("Object", startLine, startColumn);
                        break;
                    default:
                        throw UnexpectedCharacter("was expecting comma to separate Object entries");
                }
            }
        }

        private JsonArray ReadArray()
        {
            EnterNesting(_line, Column(_position));
            try
            {
                return ReadArrayBody();
            }
            finally
            {
                _depth--;
            }
        }

        private JsonArray ReadArrayBody()
        {
            var (startLine, startColumn) = (_line, Column(_position));
            _position++;
            var result = new JsonArray();

            SkipWhitespaceInside("Array", startLine, startColumn);
            switch (Peek)
            {
                case ']':
                    _position++;
                    return result;
                case '}':
                    throw MismatchedCloseMarker('}', "Array", startLine, startColumn);
            }

            while (true)
            {
                result.Add(ReadValue());

                SkipWhitespaceInside("Array", startLine, startColumn);
                switch (Peek)
                {
                    case ']':
                        _position++;
                        return result;
                    case '}':
                        throw MismatchedCloseMarker('}', "Array", startLine, startColumn);
                    case ',':
                        _position++;
                        SkipWhitespaceInside("Array", startLine, startColumn);
                        break;
                    default:
                        throw UnexpectedCharacter("was expecting comma to separate Array entries");
                }
            }
        }

        private string ReadString()
        {
            _position++;
            var value = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw Error("Unexpected end-of-input: was expecting closing quote for a string value", _line, Column(_position));
                }

                var c = _text[_position];
                if (c == '"')
                {
                    _position++;
                    return value.ToString();
                }

                if (c < 0x20)
                {
                    throw Error(
                        $"Illegal unquoted character ({Describe(c)}): has to be escaped using backslash to be included in string value",
                        _line, Column(_position));
                }

                _position++;
                value.Append(c == '\\' ? ReadEscape() : c);
            }
        }

        private char ReadEscape()
        {
            if (AtEnd)
            {
                throw Error("Unexpected end-of-input in character escape sequence", _line, Column(_position));
            }

            var c = _text[_position++];
            switch (c)
            {
                case '"':
                case '\\':
                case '/':
                    return c;
                case 'b':
                    return '\b';
                case 'f':
                    return '\f';
                case 'n':
                    return '\n';
                case 'r':
                    return '\r';
                case 't':
                    return '\t';
                case 'u':
                    var code = 0;
                    for (var i = 0; i < 4; i++)
                    {
                        if (AtEnd)
                        {
                            throw Error("Unexpected end-of-input in character escape sequence", _line, Column(_position));
                        }

                        var digit = HexValue(Peek);
                        if (digit < 0)
                        {
                            throw UnexpectedCharacter("expected a hex-digit for character escape sequence");
                        }

                        code = (code << 4) | digit;
                        _position++;
                    }
                    return (char)code;
                default:
                    throw Error($"Unrecognized character escape {Describe(c)}", _line, Column(_position - 1));
            }
        }

        private JsonNode ReadNumber()
        {
            var start = _position;
            if (Peek == '-')
            {
                _position++;
                if (AtEnd || !IsDigit(Peek))
                {
                    throw NumberError("expected digit (0-9) to follow minus sign, for valid numeric value");
                }
            }

            if (Peek == '0' && _position + 1 < _text.Length && IsDigit(_text[_position + 1]))
            {
                throw Error("Invalid numeric value: Leading zeroes not allowed", _line, Column(_position + 1));
            }

            SkipDigits();
            if (!AtEnd && Peek == '.')
            {
                _position++;
                if (AtEnd || !IsDigit(Peek))
                {
                    throw NumberError("Decimal point not followed by a digit");
                }
                SkipDigits();
            }

            if (!AtEnd && Peek is 'e' or 'E')
            {
                _position++;
                if (!AtEnd && Peek is '+' or '-')
                {
                    _position++;
                }
                if (AtEnd || !IsDigit(Peek))
                {
                    throw NumberError("Exponent indicator not followed by a digit");
                }
                SkipDigits();
            }

            // Keep the number's own text so it is written back (and compared) exactly as given.
            return JsonNode.Parse(_text.Substring(start, _position - start))!;
        }

        private JsonValue? ReadLiteral()
        {
            var start = _position;
            while (!AtEnd && (char.IsLetterOrDigit(Peek) || Peek is '_' or '$'))
            {
                _position++;
            }

            var token = _text.Substring(start, _position - start);
            switch (token)
            {
                case "true":
                    return JsonValue.Create(true);
                case "false":
                    return JsonValue.Create(false);
                case "null":
                    return null;
            }

            if (token.Length > MaxReportedTokenLength)
            {
                token = token.Remove(MaxReportedTokenLength) + "...";
            }

            throw Error($"Unrecognized token '{token}': was expecting {ValidValues}", _line, Column(start));
        }

        private void SkipDigits()
        {
            while (!AtEnd && IsDigit(Peek))
            {
                _position++;
            }
        }

        private void SkipWhitespace()
        {
            while (!AtEnd)
            {
                switch (Peek)
                {
                    case ' ':
                    case '\t':
                        _position++;
                        break;
                    case '\r':
                    case '\n':
                        // \r\n is one line break.
                        if (Peek == '\r' && _position + 1 < _text.Length && _text[_position + 1] == '\n')
                        {
                            _position++;
                        }
                        _position++;
                        _line++;
                        _lineStart = _position;
                        break;
                    default:
                        return;
                }
            }
        }

        private void SkipWhitespaceInside(string container, int startLine, int startColumn)
        {
            SkipWhitespace();
            if (AtEnd)
            {
                throw Error(
                    $"Unexpected end-of-input: expected close marker for {container} (start marker at {Location(startLine, startColumn)})",
                    _line, Column(_position));
            }
        }

        private InternalInvalidParameterException MismatchedCloseMarker(char marker, string container, int startLine, int startColumn) =>
            Error(
                $"Unexpected close marker '{marker}': expected '{(container == "Object" ? '}' : ']')}' (for {container} starting at {Location(startLine, startColumn)})",
                _line, Column(_position));

        private InternalInvalidParameterException UnexpectedCharacter(string expectation) =>
            Error($"Unexpected character ({Describe(Peek)}): {expectation}", _line, Column(_position));

        private InternalInvalidParameterException NumberError(string reason) =>
            AtEnd
                ? Error("Unexpected end-of-input in a Number value", _line, Column(_position))
                : Error($"Unexpected character ({Describe(Peek)}) in numeric value: {reason}", _line, Column(_position));

        private int Column(int position) => position - _lineStart + 1;

        private static InternalInvalidParameterException Error(string reason, int line, int column) =>
            Invalid($"failed to parse JSON. {reason}\n at {Location(line, column)}");

        private static string Location(int line, int column) =>
            $"[Source: {RedactedSource}; line: {line}, column: {column}]";

        private static string Describe(char c)
        {
            int code = c;
            if (char.IsControl(c))
            {
                return $"(CTRL-CHAR, code {code})";
            }

            return code > 255
                ? $"'{c}' (code {code} / 0x{code:x})"
                : $"'{c}' (code {code})";
        }

        private static bool IsDigit(char c) => c is >= '0' and <= '9';

        private static int HexValue(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1
        };
    }

    /// <summary>
    /// Writes <paramref name="node"/> as compact JSON, escaping strings as Jackson does. SNS checks a
    /// policy's rules against this form, so its error locations count columns in it.
    /// </summary>
    private static string WriteCompact(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(node);
        return builder.ToString();

        void Write(JsonNode? value)
        {
            switch (value)
            {
                case null:
                    builder.Append("null");
                    break;

                case JsonObject obj:
                    builder.Append('{');
                    var firstProperty = true;
                    foreach (var (name, child) in obj)
                    {
                        if (!firstProperty)
                        {
                            builder.Append(',');
                        }
                        firstProperty = false;
                        WriteString(name);
                        builder.Append(':');
                        Write(child);
                    }
                    builder.Append('}');
                    break;

                case JsonArray array:
                    builder.Append('[');
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (i > 0)
                        {
                            builder.Append(',');
                        }
                        Write(array[i]);
                    }
                    builder.Append(']');
                    break;

                default:
                    if (value.GetValueKind() == JsonValueKind.String)
                    {
                        WriteString(value.GetValue<string>());
                    }
                    else
                    {
                        builder.Append(value.ToJsonString());
                    }
                    break;
            }
        }

        void WriteString(string value)
        {
            builder.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    case < (char)0x20:
                        builder.Append("\\u00").Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
                        break;
                    default:
                        builder.Append(c);
                        break;
                }
            }
            builder.Append('"');
        }
    }
}
