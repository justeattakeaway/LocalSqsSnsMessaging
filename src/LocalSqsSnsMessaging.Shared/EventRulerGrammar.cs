using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LocalSqsSnsMessaging;

/// <summary>
/// The shape of a pattern once its <c>$or</c> branches are expanded: one rule per combination of
/// branches, each mapping a leaf key's dotted path to how many values it matches and how deeply it
/// is nested.
/// </summary>
internal sealed class EventRulerRules
{
    public List<Dictionary<string, (int Values, int Depth)>> Rules { get; } = [];

    /// <summary>Whether any key has a nested object rather than an array of values.</summary>
    public bool IsNested { get; set; }
}

/// <summary>
/// Checks a pattern against the grammar SNS filter policies and EventBridge event patterns share (the
/// AWS Event Ruler rule compiler), refusing it with <see cref="EventRulerPatternException"/> and the
/// compiler's reason. A refusal names the position the compiler had read to, as Jackson reports it:
/// <c>line</c>/<c>column</c> in the text being compiled, which it quotes.
/// </summary>
/// <remarks>
/// The text must already be known to be JSON (see <see cref="EventRulerJsonReader"/>). SNS compiles a
/// compact rewrite of the policy, so its positions count columns in that; EventBridge compiles the
/// pattern as it was sent, whitespace and line breaks included.
/// </remarks>
internal sealed class EventRulerGrammar
{
    private enum PolicyToken
    {
        None,
        StartObject,
        EndObject,
        StartArray,
        EndArray,
        FieldName,
        String,
        Number,
        True,
        False,
        Null
    }

    private enum AnythingButKind
    {
        Values,
        EqualsIgnoreCase,
        Prefix,
        Suffix,
        Wildcard
    }

    private const string Or = "$or";

    // Field names reserved for operators, which can't be used as keys inside an $or branch.
    private static readonly HashSet<string> ReservedInOr = new(StringComparer.Ordinal)
    {
        "exactly", "equals-ignore-case", "prefix", "suffix", "anything-but", "exists", "wildcard", "numeric", "cidr",
        "=", "<", "<=", ">", ">=",
        "regex", "not-wildcard", "not-equals-ignore-case",
        "date-after", "date-on-or-after", "date-before", "date-on-or-before", "in-date-range",
        "ip-address-in-range", "ip-address-not-in-range"
    };

    // Jackson quotes at most this much of the source in an error location.
    private const int MaxQuotedSourceLength = 500;

    private readonly PolicyTokenReader _reader;
    private readonly string _json;
    private readonly bool _orAsFieldName;
    private readonly EventRulerRules _result = new();

    private EventRulerGrammar(string json, bool orAsFieldName)
    {
        _json = json;
        _orAsFieldName = orAsFieldName;
        _reader = new PolicyTokenReader(json);
    }

    /// <summary>
    /// Compiles <paramref name="json"/>, throwing <see cref="EventRulerPatternException"/> when it
    /// breaks the grammar.
    /// </summary>
    /// <param name="json">The pattern's JSON text.</param>
    /// <param name="orAsFieldName">
    /// Whether <c>$or</c> is an ordinary field name rather than the or-relationship keyword, as Event
    /// Ruler reads it when a pattern's <c>$or</c> is not a valid or-relationship.
    /// </param>
    public static EventRulerRules Compile(string json, bool orAsFieldName = false)
    {
        var grammar = new EventRulerGrammar(json, orAsFieldName);
        if (grammar._reader.Next() != PolicyToken.StartObject)
        {
            throw grammar.Refuse("Filter is not an object");
        }

        grammar.ParseObject(grammar._result.Rules, [], insideOr: false);
        return grammar._result;
    }

    private void ParseObject(List<Dictionary<string, (int, int)>> rules, List<string> path, bool insideOr)
    {
        var fieldsPresent = false;
        while (_reader.Next() != PolicyToken.EndObject)
        {
            fieldsPresent = true;
            var name = _reader.CurrentName;
            if (!_orAsFieldName && string.Equals(name, Or, StringComparison.Ordinal))
            {
                ParseOr(rules, path);
                continue;
            }

            if (insideOr && ReservedInOr.Contains(name))
            {
                throw Refuse($"{name} is Ruler reserved fieldName which cannot be used inside {Or}.");
            }

            switch (_reader.Next())
            {
                case PolicyToken.StartObject:
                    _result.IsNested = true;
                    path.Add(name);
                    ParseObject(rules, path, insideOr);
                    path.RemoveAt(path.Count - 1);
                    break;

                case PolicyToken.StartArray:
                    ParseValues(rules, path, name);
                    break;

                default:
                    throw Refuse($"\"{name}\" must be an object or an array");
            }
        }

        if (!fieldsPresent)
        {
            throw Refuse("Empty objects are not allowed");
        }
    }

    private void ParseOr(List<Dictionary<string, (int, int)>> rules, List<string> path)
    {
        // Each branch extends a copy of the rules so far; together the branches replace them.
        var before = rules.Select(rule => new Dictionary<string, (int, int)>(rule, StringComparer.Ordinal)).ToList();
        rules.Clear();

        if (_reader.Next() != PolicyToken.StartArray)
        {
            throw RefuseOrRelationship($"It must be an Array followed with {Or}.");
        }

        var branches = 0;
        PolicyToken token;
        while ((token = _reader.Next()) != PolicyToken.EndArray)
        {
            branches++;
            if (token != PolicyToken.StartObject)
            {
                throw RefuseOrRelationship($"Only JSON object is allowed in array of {Or} relationship.");
            }

            var branchRules = before.Select(rule => new Dictionary<string, (int, int)>(rule, StringComparer.Ordinal)).ToList();
            if (branchRules.Count == 0)
            {
                branchRules.Add(new Dictionary<string, (int, int)>(StringComparer.Ordinal));
            }

            ParseObject(branchRules, path, insideOr: true);
            rules.AddRange(branchRules);
        }

        if (branches < 2)
        {
            throw RefuseOrRelationship($"There must have at least 2 Objects in {Or} relationship.");
        }
    }

    private void ParseValues(List<Dictionary<string, (int, int)>> rules, List<string> path, string name)
    {
        var values = 0;
        PolicyToken token;
        while ((token = _reader.Next()) != PolicyToken.EndArray)
        {
            switch (token)
            {
                case PolicyToken.StartObject:
                    ParseMatchExpression();
                    break;

                case PolicyToken.String:
                case PolicyToken.Number:
                    _ = _reader.Text;
                    break;

                case PolicyToken.True:
                case PolicyToken.False:
                case PolicyToken.Null:
                    break;

                default:
                    throw Refuse("Match value must be String, number, true, false, or null");
            }

            values++;
        }

        if (values == 0)
        {
            throw Refuse("Empty arrays are not allowed");
        }

        if (rules.Count == 0)
        {
            rules.Add(new Dictionary<string, (int, int)>(StringComparer.Ordinal));
        }

        var key = string.Join(".", path.Append(name));
        foreach (var rule in rules)
        {
            rule[key] = (values, path.Count + 1);
        }
    }

    private void ParseMatchExpression()
    {
        if (_reader.Next() != PolicyToken.FieldName)
        {
            throw Refuse("Match expression name not found");
        }

        var matchType = _reader.CurrentName;
        switch (matchType)
        {
            case "prefix":
            case "suffix":
                var affix = _reader.Next();
                if (affix == PolicyToken.StartObject)
                {
                    ParseAffixEqualsIgnoreCase(matchType);
                    return;
                }

                ExpectString(affix, $"{matchType} match pattern must be a string");
                ExpectEndOfExpression();
                return;

            case "numeric":
                if (_reader.Next() != PolicyToken.StartArray)
                {
                    throw Refuse("Value of numeric must be an array.");
                }

                ParseNumericExpression();
                if (_reader.Next() != PolicyToken.EndObject)
                {
                    throw TooManyElements();
                }
                return;

            case "anything-but":
                ParseAnythingBut();
                return;

            case "exists":
                if (_reader.Next() is not (PolicyToken.True or PolicyToken.False))
                {
                    throw Refuse("exists match pattern must be either true or false.");
                }

                ExpectEndOfExpression();
                return;

            case "cidr":
                ExpectString(_reader.Next(), "prefix match pattern must be a string");
                ValidateCidr(_reader.Text);
                ExpectEndOfExpression();
                return;

            case "equals-ignore-case":
                ExpectString(_reader.Next(), "equals-ignore-case match pattern must be a string");
                ExpectEndOfExpression();
                return;

            case "wildcard":
                ExpectString(_reader.Next(), "wildcard match pattern must be a string");
                ValidateWildcard(_reader.Text);
                ExpectEndOfExpression();
                return;

            default:
                throw Refuse($"Unrecognized match type {matchType}");
        }
    }

    private void ParseAffixEqualsIgnoreCase(string matchType)
    {
        var title = matchType == "prefix" ? "Prefix" : "Suffix";
        if (_reader.Next() != PolicyToken.FieldName)
        {
            throw Refuse($"{title} expression name not found");
        }

        if (!string.Equals(_reader.CurrentName, "equals-ignore-case", StringComparison.Ordinal))
        {
            throw Refuse($"Unsupported {matchType} pattern: {_reader.CurrentName}");
        }

        ExpectString(_reader.Next(), "equals-ignore-case match pattern must be a string");
        ExpectEndOfExpression();
        ExpectEndOfExpression();
    }

    private void ParseAnythingBut()
    {
        var kind = AnythingButKind.Values;
        var token = _reader.Next();
        if (token == PolicyToken.StartObject)
        {
            if (_reader.Next() != PolicyToken.FieldName)
            {
                throw Refuse("Anything-But expression name not found");
            }

            kind = _reader.CurrentName switch
            {
                "equals-ignore-case" => AnythingButKind.EqualsIgnoreCase,
                "prefix" => AnythingButKind.Prefix,
                "suffix" => AnythingButKind.Suffix,
                "wildcard" => AnythingButKind.Wildcard,
                _ => throw Refuse($"Unsupported anything-but pattern: {_reader.CurrentName}")
            };
            token = _reader.Next();
        }

        if (token is not (PolicyToken.StartArray or PolicyToken.String or PolicyToken.Number))
        {
            throw Refuse("Value of anything-but must be an array or single string/number value.");
        }

        switch (kind, token)
        {
            case (AnythingButKind.Values, PolicyToken.StartArray):
                ParseAnythingButValues();
                break;

            case (AnythingButKind.Values, PolicyToken.Number):
                // Thrown outside the compiler's own error handling, so it carries no location.
                if (!IsComparableNumber(_reader.Text))
                {
                    throw new EventRulerPatternException($"Cannot compare number : {_reader.Text}");
                }
                break;

            case (AnythingButKind.Values, _):
                _ = _reader.Text;
                break;

            case (_, PolicyToken.StartArray):
                ParseAnythingButOperands(kind);
                break;

            default:
                ParseAnythingButOperand(kind, token, refuseInList: false);
                break;
        }

        if (_reader.Next() != PolicyToken.EndObject)
        {
            throw TooManyElements();
        }

        if (kind != AnythingButKind.Values && _reader.Next() != PolicyToken.EndObject)
        {
            throw TooManyElements();
        }
    }

    private void ParseAnythingButValues()
    {
        var (hasNumber, hasString) = (false, false);
        PolicyToken token;
        while ((token = _reader.Next()) != PolicyToken.EndArray)
        {
            switch (token)
            {
                case PolicyToken.String:
                    _ = _reader.Text;
                    hasString = true;
                    break;

                case PolicyToken.Number:
                    if (!IsComparableNumber(_reader.Text))
                    {
                        throw RefuseInList($"Cannot compare number : {_reader.Text}");
                    }
                    hasNumber = true;
                    break;

                default:
                    throw RefuseInList("Inside anything but list, start|null|boolean is not supported.");
            }
        }

        if (hasNumber == hasString)
        {
            throw Refuse("Inside anything but list, either all values are number or string, mixed type is not supported");
        }
    }

    private void ParseAnythingButOperands(AnythingButKind kind)
    {
        var wildcards = new List<string>();
        PolicyToken token;
        while ((token = _reader.Next()) != PolicyToken.EndArray)
        {
            var operand = ParseAnythingButOperand(kind, token, refuseInList: true);
            if (kind == AnythingButKind.Wildcard)
            {
                wildcards.Add(operand);
            }
        }

        foreach (var wildcard in wildcards)
        {
            ValidateWildcard(wildcard);
        }
    }

    private string ParseAnythingButOperand(AnythingButKind kind, PolicyToken token, bool refuseInList)
    {
        if (token != PolicyToken.String)
        {
            var reason = kind switch
            {
                AnythingButKind.EqualsIgnoreCase => "Inside anything-but/equals-ignore-case list, number|start|null|boolean is not supported.",
                AnythingButKind.Wildcard => "wildcard match pattern must be a string",
                _ => "prefix/suffix match pattern must be a string"
            };
            throw refuseInList ? RefuseInList(reason) : Refuse(reason);
        }

        var operand = _reader.Text;
        if (kind is AnythingButKind.Prefix or AnythingButKind.Suffix && operand.Length == 0)
        {
            throw refuseInList ? RefuseInList("Null prefix/suffix not allowed") : Refuse("Null prefix/suffix not allowed");
        }

        if (kind == AnythingButKind.Wildcard && !refuseInList)
        {
            ValidateWildcard(operand);
        }

        return operand;
    }

    private void ParseNumericExpression()
    {
        if (_reader.Next() != PolicyToken.String)
        {
            throw Refuse($"Invalid member in numeric match: {_reader.Text}");
        }

        var comparison = _reader.Text;
        var token = _reader.Next();
        switch (comparison)
        {
            case "=":
            case "<":
            case "<=":
                if (token != PolicyToken.Number)
                {
                    throw Refuse(comparison == "=" ? "Value of equals must be numeric" : $"Value of {comparison} must be numeric");
                }

                var value = _reader.Text;
                if (_reader.Next() != PolicyToken.EndArray)
                {
                    throw TooManyElements();
                }

                EnsureComparable(value);
                return;

            case ">":
            case ">=":
                if (token != PolicyToken.Number)
                {
                    throw Refuse($"Value of {comparison} must be numeric");
                }

                var bottom = _reader.Text;
                token = _reader.Next();
                if (token == PolicyToken.EndArray)
                {
                    EnsureComparable(bottom);
                    return;
                }

                ParseRangeTop(token, bottom);
                return;

            default:
                throw Refuse($"Unrecognized numeric range operator: {comparison}");
        }
    }

    private void ParseRangeTop(PolicyToken token, string bottom)
    {
        if (token != PolicyToken.String)
        {
            throw Refuse($"Bad value in numeric range: {_reader.Text}");
        }

        var comparison = _reader.Text;
        if (comparison is not ("<" or "<="))
        {
            throw Refuse($"Bad numeric range operator: {comparison}");
        }

        if (_reader.Next() != PolicyToken.Number)
        {
            throw Refuse($"Value of {comparison} must be numeric");
        }

        var top = _reader.Text;
        if (_reader.Next() != PolicyToken.EndArray)
        {
            throw Refuse("Too many terms in numeric range expression");
        }

        EnsureComparable(bottom);
        EnsureComparable(top);
        if (ParseNumber(bottom) >= ParseNumber(top))
        {
            throw Refuse("Bottom must be less than top");
        }
    }

    private void EnsureComparable(string number)
    {
        if (!IsComparableNumber(number))
        {
            throw Refuse($"Cannot compare number : {number}");
        }
    }

    private void ExpectString(PolicyToken token, string reason)
    {
        if (token != PolicyToken.String)
        {
            throw Refuse(reason);
        }

        _ = _reader.Text;
    }

    private void ExpectEndOfExpression()
    {
        if (_reader.Next() != PolicyToken.EndObject)
        {
            throw Refuse("Only one key allowed in match expression");
        }
    }

    private void ValidateWildcard(string pattern)
    {
        // Positions count UTF-8 bytes of the pattern as the compiler quotes it.
        var bytes = Encoding.UTF8.GetBytes("\"" + pattern + "\"");
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == '*')
            {
                if (i + 1 < bytes.Length && bytes[i + 1] == '*')
                {
                    throw Refuse($"Consecutive wildcard characters at pos {i}");
                }
            }
            else if (bytes[i] == '\\')
            {
                if (i + 1 < bytes.Length && bytes[i + 1] is (byte)'*' or (byte)'\\')
                {
                    i++;
                    continue;
                }

                throw Refuse($"Invalid escape character at pos {i}");
            }
        }
    }

    // An invalid CIDR is refused outside the compiler's own error handling, so it carries no location.
    private static void ValidateCidr(string cidr)
    {
        // Split as Java's String.split does, dropping trailing empty parts.
        var parts = cidr.Split('/').ToList();
        while (parts.Count > 0 && parts[parts.Count - 1].Length == 0)
        {
            parts.RemoveAt(parts.Count - 1);
        }

        if (parts.Count != 2)
        {
            throw new EventRulerPatternException("Malformed CIDR, one '/' required");
        }

        if (!int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var maskBits))
        {
            throw new EventRulerPatternException("Malformed CIDR, mask bits must be an integer");
        }

        if (maskBits < 0)
        {
            throw new EventRulerPatternException("Malformed CIDR, mask bits must not be negative");
        }

        var address = parts[0];
        var isV4 = IsDottedQuad(address);
        if (!isV4 && !(address.Any(c => c == ':') && address.All(c => c == ':' || Uri.IsHexDigit(c))))
        {
            throw new EventRulerPatternException($"Nonstandard IP address: {address}");
        }

        if (!IPAddress.TryParse(address, out var parsed) ||
            parsed.AddressFamily != (isV4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6))
        {
            throw new EventRulerPatternException($"Invalid IP address: {address}");
        }

        if (isV4 ? maskBits > 31 : maskBits > 127)
        {
            throw new EventRulerPatternException(isV4 ? "IPv4 mask bits must be < 32" : "IPv6 mask bits must be < 128");
        }
    }

    private static bool IsDottedQuad(string address)
    {
        var octets = address.Split('.');
        return octets.Length == 4 && octets.All(o => o.Length > 0 && o.All(c => c is >= '0' and <= '9'));
    }

    private static bool IsComparableNumber(string number)
    {
        var value = ParseNumber(number);
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static double ParseNumber(string number) =>
        double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;

    private EventRulerPatternException TooManyElements() => Refuse("Too many elements in numeric expression");

    private EventRulerPatternException Refuse(string reason) => new(Located(reason));

    private EventRulerPatternException RefuseOrRelationship(string reason) => new(Located(reason), isOrRelationship: true);

    // Inside these lists the compiler rethrows its own refusal, so the location is reported twice.
    private EventRulerPatternException RefuseInList(string reason) => Refuse(Located(reason));

    private string Located(string reason) =>
        $"{reason}\n at [Source: {QuoteSource(_json)}; line: {_reader.Line}, column: {_reader.Column}]";

    /// <summary>Quotes <paramref name="text"/> as Jackson does in an error location: at most 500 characters of it.</summary>
    public static string QuoteSource(string text) =>
        text.Length <= MaxQuotedSourceLength
            ? $"(String)\"{text}\""
            : $"(String)\"{text.Substring(0, MaxQuotedSourceLength)}\"[truncated {text.Length - MaxQuotedSourceLength} chars]";

    /// <summary>
    /// A streaming reader over well-formed JSON that reports the same position as Jackson's parser at
    /// every token: a field name is read together with the start of its value, and a string value
    /// is only read past its opening quote until its text is asked for.
    /// </summary>
    private sealed class PolicyTokenReader(string json)
    {
        private readonly Stack<bool> _inObject = new();
        private int _position;
        private int _line = 1;
        private int _lineStart;
        private bool _stringPending;
        private PolicyToken _valueAfterName;
        private string _text = string.Empty;

        public PolicyToken Current { get; private set; }

        public string CurrentName { get; private set; } = string.Empty;

        /// <summary>The 1-based line the reader has read up to.</summary>
        public int Line => _line;

        /// <summary>The 1-based column, within <see cref="Line"/>, the reader has read up to.</summary>
        public int Column => _position - _lineStart + 1;

        /// <summary>The current token's text, reading the rest of a string value if needed.</summary>
        public string Text
        {
            get
            {
                if (_stringPending && Current == PolicyToken.String)
                {
                    _text = ReadStringRest();
                }

                return Current switch
                {
                    PolicyToken.FieldName => CurrentName,
                    PolicyToken.StartObject => "{",
                    PolicyToken.EndObject => "}",
                    PolicyToken.StartArray => "[",
                    PolicyToken.EndArray => "]",
                    PolicyToken.None => string.Empty,
                    _ => _text
                };
            }
        }

        public PolicyToken Next()
        {
            if (Current == PolicyToken.FieldName)
            {
                return Enter(_valueAfterName);
            }

            if (_stringPending)
            {
                _text = ReadStringRest();
            }

            SkipWhitespace();
            if (_position >= json.Length)
            {
                return Current = PolicyToken.None;
            }

            if (json[_position] == ',')
            {
                _position++;
                SkipWhitespace();
            }

            var c = json[_position];
            if (c is '}' or ']')
            {
                _position++;
                _inObject.Pop();
                return Current = c == '}' ? PolicyToken.EndObject : PolicyToken.EndArray;
            }

            if (_inObject.Count > 0 && _inObject.Peek())
            {
                _position++;
                CurrentName = ReadStringRest();
                SkipWhitespace();
                _position++; // the colon
                SkipWhitespace();
                _valueAfterName = ReadValueStart();
                return Current = PolicyToken.FieldName;
            }

            return Enter(ReadValueStart());
        }

        private PolicyToken Enter(PolicyToken token)
        {
            if (token is PolicyToken.StartObject or PolicyToken.StartArray)
            {
                _inObject.Push(token == PolicyToken.StartObject);
            }

            return Current = token;
        }

        private PolicyToken ReadValueStart()
        {
            var c = json[_position++];
            switch (c)
            {
                case '"':
                    _stringPending = true;
                    return PolicyToken.String;
                case '{':
                    return PolicyToken.StartObject;
                case '[':
                    return PolicyToken.StartArray;
                case 't':
                    return Literal("true", PolicyToken.True);
                case 'f':
                    return Literal("false", PolicyToken.False);
                case 'n':
                    return Literal("null", PolicyToken.Null);
            }

            var start = _position - 1;
            while (_position < json.Length && json[_position] is (>= '0' and <= '9') or '-' or '+' or '.' or 'e' or 'E')
            {
                _position++;
            }

            _text = json.Substring(start, _position - start);
            return PolicyToken.Number;
        }

        private void SkipWhitespace()
        {
            while (_position < json.Length)
            {
                switch (json[_position])
                {
                    case ' ':
                    case '\t':
                        _position++;
                        break;
                    case '\r':
                    case '\n':
                        // \r\n is one line break.
                        if (json[_position] == '\r' && _position + 1 < json.Length && json[_position + 1] == '\n')
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

        private PolicyToken Literal(string literal, PolicyToken token)
        {
            _position += literal.Length - 1;
            _text = literal;
            return token;
        }

        // Reads from just after an opening quote to just after the closing one.
        private string ReadStringRest()
        {
            _stringPending = false;
            var value = new StringBuilder();
            while (json[_position] != '"')
            {
                var c = json[_position++];
                if (c != '\\')
                {
                    value.Append(c);
                    continue;
                }

                var escaped = json[_position++];
                value.Append(escaped switch
                {
                    'b' => '\b',
                    'f' => '\f',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'u' => (char)Convert.ToInt32(json.Substring(_position, 4), 16),
                    _ => escaped
                });
                if (escaped == 'u')
                {
                    _position += 4;
                }
            }

            _position++;
            return value.ToString();
        }
    }
}
