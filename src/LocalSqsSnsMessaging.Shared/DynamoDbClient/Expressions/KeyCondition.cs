namespace LocalSqsSnsMessaging.DynamoDb.Expressions;

/// <summary>A key condition's operator: the comparisons, <c>BETWEEN</c> and <c>begins_with</c>.</summary>
internal enum KeyOperator
{
    Equal,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    Between,
    BeginsWith,
}

/// <summary>One comparison of a key condition, on one key attribute.</summary>
internal sealed record KeyComparison(string Attribute, KeyOperator Operator, string Written, IReadOnlyList<DynamoValue> Operands);

/// <summary>
/// A Query's key condition, resolved against the key schema it queries: one partition value, and
/// at most one condition on the sort key. For a schema with several RANGE attributes, the sort
/// condition is equality on a prefix of them, then at most one comparison on the next.
/// </summary>
internal sealed class KeyCondition
{
    private const string Member = "KeyConditionExpression";

    private readonly IReadOnlyList<DynamoValue>? _rangePrefix;

    private KeyCondition(DynamoValue partition, KeyComparison? sort, IReadOnlyList<DynamoValue>? rangePrefix)
    {
        Partition = partition;
        Sort = sort;
        _rangePrefix = rangePrefix;
    }

    /// <summary>The partition value: the HASH attribute's, or (for several) the list of theirs in schema order.</summary>
    public DynamoValue Partition { get; }

    /// <summary>The comparison on the sort key; for several RANGE attributes, on the one after the equality prefix.</summary>
    public KeyComparison? Sort { get; }

    public const string Required = "Either the KeyConditions or KeyConditionExpression parameter must be specified in the request.";

    /// <summary>Whether a sort key value satisfies the sort condition (or there is none).</summary>
    public bool MatchesSort(DynamoValue? actual)
    {
        if (_rangePrefix is null)
        {
            return Sort is null || (actual is not null && Satisfies(Sort, actual));
        }

        // Several RANGE attributes: the sort value is the list of their values in schema order.
        if (actual is null)
        {
            return _rangePrefix.Count == 0 && Sort is null;
        }

        var values = actual.L;
        for (var i = 0; i < _rangePrefix.Count; i++)
        {
            if (DynamoKeyOrder.Compare(values[i], _rangePrefix[i]) != 0)
            {
                return false;
            }
        }

        return Sort is null || Satisfies(Sort, values[_rangePrefix.Count]);
    }

    private static bool Satisfies(KeyComparison comparison, DynamoValue actual)
    {
        var first = comparison.Operands[0];
        var order = DynamoKeyOrder.Compare(actual, first);
        return comparison.Operator switch
        {
            KeyOperator.Equal => order == 0,
            KeyOperator.Less => order < 0,
            KeyOperator.LessOrEqual => order <= 0,
            KeyOperator.Greater => order > 0,
            KeyOperator.GreaterOrEqual => order >= 0,
            KeyOperator.Between => order >= 0 && DynamoKeyOrder.Compare(actual, comparison.Operands[1]) <= 0,
            _ => ConditionValues.BeginsWith(actual, first),
        };
    }

    /// <summary>Parses a <c>KeyConditionExpression</c> into its comparisons.</summary>
    public static List<KeyComparison> Parse(string expression, ExpressionAttributes attributes) =>
        new Parser(expression, attributes).Run();

    /// <summary>
    /// Refuses an operand its operator can't take, when one comes before any undefined
    /// <c>#name</c> or <c>:value</c>: DynamoDB reads the comparisons in the order they are
    /// written, so whichever of the two defects comes first is the one it reports. Every other
    /// defect is left to <see cref="ExpressionAttributes.CheckAllReferenced"/> and <see cref="Parse"/>.
    /// </summary>
    public static void CheckOperandsBeforeUndefined(string expression, ExpressionAttributes attributes)
    {
        var parser = new Parser(expression, attributes);
        try
        {
            parser.Run();
        }
        catch (DynamoDbException) when (parser.OperandFault is null)
        {
            // Left to the checks that come after.
        }
    }

    /// <summary>Translates legacy <c>KeyConditions</c> into comparisons.</summary>
    public static List<KeyComparison> FromLegacy(Dictionary<string, Model.Condition> conditions)
    {
        var comparisons = new List<KeyComparison>();
        foreach (var (attribute, condition) in conditions)
        {
            var operands = condition.AttributeValueList ?? [];
            LegacyConditions.CheckComparison(condition.ComparisonOperator ?? string.Empty, operands);
            var op = condition.ComparisonOperator switch
            {
                "EQ" => KeyOperator.Equal,
                "LT" => KeyOperator.Less,
                "LE" => KeyOperator.LessOrEqual,
                "GT" => KeyOperator.Greater,
                "GE" => KeyOperator.GreaterOrEqual,
                "BETWEEN" => KeyOperator.Between,
                "BEGINS_WITH" => KeyOperator.BeginsWith,
                _ => throw DynamoDbErrors.InvalidParameter("Attempted conditional constraint is not an indexable operation"),
            };
            comparisons.Add(new KeyComparison(attribute, op, condition.ComparisonOperator!, operands));
        }

        return comparisons;
    }

    /// <summary>
    /// Resolves the comparisons against <paramref name="schema"/>: every partition attribute
    /// compared for equality, and each sort attribute at most once (with several, an equality
    /// on every one before the last one compared).
    /// </summary>
    public static KeyCondition Resolve(List<KeyComparison> comparisons, DynamoKeySchema schema, Func<string, DynamoType?> declaredType)
    {
        var keyCount = schema.Hash.Count + schema.Range.Count;
        if (comparisons.Count > keyCount)
        {
            throw DynamoDbErrors.Validation($"The number of query conditions ({comparisons.Count.ToString(CultureInfo.InvariantCulture)}) exceeds the number of key attributes defined in the schema ({keyCount.ToString(CultureInfo.InvariantCulture)})");
        }

        var hashes = new DynamoValue?[schema.Hash.Count];
        var ranges = new KeyComparison?[schema.Range.Count];
        var nonKey = false;
        foreach (var comparison in comparisons)
        {
            var atHash = IndexOf(schema.Hash, comparison.Attribute);
            var atRange = IndexOf(schema.Range, comparison.Attribute);
            if (atHash < 0 && atRange < 0)
            {
                nonKey = true;
                continue;
            }

            var keyType = atHash >= 0 ? schema.Hash[atHash].Type : schema.Range[atRange].Type;
            foreach (var operand in comparison.Operands)
            {
                if (operand.Type != keyType)
                {
                    throw DynamoDbErrors.InvalidParameter("Condition parameter type does not match schema type");
                }

                if (operand.Type == DynamoType.N)
                {
                    DynamoValueRules.ParseNumber(operand.N, enveloped: false);
                }
            }

            if (atHash >= 0)
            {
                if (comparison.Operator != KeyOperator.Equal)
                {
                    throw DynamoDbErrors.Validation("Query key condition not supported");
                }

                hashes[atHash] = DynamoValueRules.Canonicalize(comparison.Operands[0], enveloped: false);
            }
            else
            {
                ranges[atRange] = comparison with { Operands = comparison.Operands.Select(o => DynamoValueRules.Canonicalize(o, enveloped: false)).ToList() };
            }
        }

        var unconstrainedHash = schema.Hash.Where((_, i) => hashes[i] is null).Select(a => a.Name).ToList();
        if (nonKey || unconstrainedHash.Count == schema.Hash.Count)
        {
            var missed = unconstrainedHash.FirstOrDefault() ?? schema.Range.Where((_, i) => ranges[i] is null).Select(a => a.Name).FirstOrDefault();
            throw missed is null
                ? DynamoDbErrors.Validation("Query key condition not supported")
                : DynamoDbErrors.Validation($"Query condition missed key schema element: {missed}");
        }

        if (unconstrainedHash.Count > 0)
        {
            throw DynamoDbErrors.Validation($"HASH key attributes {string.Join(", ", unconstrainedHash)} must have equality conditions specified in the query");
        }

        _ = declaredType;
        var partition = schema.Hash.Count == 1 ? hashes[0]! : DynamoValue.FromList(hashes.Select(h => h!).ToList());
        if (schema.Range.Count <= 1)
        {
            return new KeyCondition(partition, ranges.FirstOrDefault(), rangePrefix: null);
        }

        // Several RANGE attributes: every one before the last one compared needs an equality.
        var last = Array.FindLastIndex(ranges, r => r is not null);
        var unequal = schema.Range.Take(Math.Max(last, 0)).Where((_, i) => ranges[i]?.Operator != KeyOperator.Equal).Select(a => a.Name).ToList();
        if (unequal.Count > 0)
        {
            throw DynamoDbErrors.Validation($"RANGE key attributes {string.Join(", ", unequal)} must have equality conditions specified in the query because a condition is present on key attribute {schema.Range[last].Name}");
        }

        return last < 0
            ? new KeyCondition(partition, null, [])
            : new KeyCondition(partition, ranges[last], ranges.Take(last).Select(r => r!.Operands[0]).ToList());
    }

    private static int IndexOf(IReadOnlyList<DynamoKeyAttribute> attributes, string name)
    {
        for (var i = 0; i < attributes.Count; i++)
        {
            if (string.Equals(attributes[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class Parser
    {
        private readonly List<(string Kind, string Text)> _tokens;
        private readonly ExpressionAttributes _attributes;
        private readonly HashSet<string> _constrained = new(StringComparer.Ordinal);
        private int _at;
        private int _depth;
        private string? _reserved;

        public Parser(string expression, ExpressionAttributes attributes)
        {
            _attributes = attributes;
            _tokens = Tokenize(expression);
        }

        /// <summary>The refusal of an operand the parse raised, if it raised one.</summary>
        public DynamoDbException? OperandFault { get; private set; }

        private static bool IsIdentifierChar(char c) =>
            c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '.' or '-' or '[' or ']';

        private static readonly string[] NonKeyOperators = ["OR", "NOT", "IN", "contains", "attribute_exists", "attribute_not_exists", "attribute_type"];

        private static DynamoDbException InvalidOperator() =>
            DynamoDbErrors.Validation("Invalid KeyConditionExpression: Invalid operator used in KeyConditionExpression");

        private static List<(string Kind, string Text)> Tokenize(string text)
        {
            if (Encoding.UTF8.GetByteCount(text) > ExpressionErrors.MaxExpressionBytes)
            {
                throw ExpressionErrors.Invalid(Member, "Expression size has exceeded the maximum allowed size;");
            }

            var tokens = new List<(string, string)>();
            var at = 0;
            while (at < text.Length)
            {
                var c = text[at];
                if (char.IsWhiteSpace(c))
                {
                    at++;
                }
                else if (c is '(' or ')' or ',' or '=')
                {
                    tokens.Add((c.ToString(), c.ToString()));
                    at++;
                }
                else if (c is '<' or '>')
                {
                    if (c == '<' && at + 1 < text.Length && text[at + 1] == '>')
                    {
                        throw InvalidOperator();
                    }

                    var width = at + 1 < text.Length && text[at + 1] == '=' ? 2 : 1;
                    tokens.Add(("cmp", text.Substring(at, width)));
                    at += width;
                }
                else if (c is ':' or '#')
                {
                    var start = at++;
                    while (at < text.Length && IsIdentifierChar(text[at]))
                    {
                        at++;
                    }

                    if (at - start < 2)
                    {
                        throw ExpressionErrors.Lexical(Member, text, start);
                    }

                    tokens.Add((c == ':' ? "value" : "name", text[start..at]));
                }
                else if (IsIdentifierChar(c))
                {
                    var start = at;
                    while (at < text.Length && IsIdentifierChar(text[at]))
                    {
                        at++;
                    }

                    var word = text[start..at];
                    if (string.Equals(word, "AND", StringComparison.OrdinalIgnoreCase))
                    {
                        tokens.Add(("and", word));
                    }
                    else if (string.Equals(word, "BETWEEN", StringComparison.OrdinalIgnoreCase))
                    {
                        tokens.Add(("between", word));
                    }
                    else if (string.Equals(word, "begins_with", StringComparison.OrdinalIgnoreCase))
                    {
                        tokens.Add(("begins_with", word));
                    }
                    else if (NonKeyOperators.Any(o => string.Equals(o, word, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw InvalidOperator();
                    }
                    else if (string.Equals(word, "size", StringComparison.Ordinal) && at < text.Length && text[at] == '(')
                    {
                        throw DynamoDbErrors.Validation("Invalid KeyConditionExpression: KeyConditionExpressions cannot contain nested operations");
                    }
                    else
                    {
                        tokens.Add(("name", word));
                    }
                }
                else
                {
                    throw ExpressionErrors.Lexical(Member, text, at);
                }
            }

            if (tokens.Count == 0)
            {
                throw ExpressionErrors.Invalid(Member, "The expression can not be empty;");
            }

            return tokens;
        }

        private DynamoDbException Syntax()
        {
            var token = _at < _tokens.Count ? _tokens[_at].Text : "<EOF>";
            var near = string.Join(" ", new[] { _at - 1, _at, _at + 1 }.Where(i => i >= 0 && i < _tokens.Count).Select(i => _tokens[i].Text));
            return ExpressionErrors.Syntax(Member, token, near);
        }

        private string? PeekKind => _at < _tokens.Count ? _tokens[_at].Kind : null;

        private void Expect(string kind)
        {
            if (PeekKind != kind)
            {
                throw Syntax();
            }

            _at++;
        }

        public List<KeyComparison> Run()
        {
            var comparisons = Conjunction();
            if (_at != _tokens.Count)
            {
                throw Syntax();
            }

            if (_reserved is not null)
            {
                throw ExpressionErrors.Invalid(Member, $"Attribute name is a reserved keyword; reserved keyword: {_reserved}");
            }

            return comparisons;
        }

        private List<KeyComparison> Conjunction()
        {
            var comparisons = Primary();
            while (PeekKind == "and")
            {
                _at++;
                comparisons.AddRange(Primary());
            }

            return comparisons;
        }

        private List<KeyComparison> Primary()
        {
            if (PeekKind == "(")
            {
                if (_depth >= ExpressionErrors.MaxDepth)
                {
                    throw DynamoDbErrors.Validation($"Invalid KeyConditionExpression: an expression may nest at most {ExpressionErrors.MaxDepth} levels of parentheses");
                }

                _at++;
                _depth++;
                var inner = Conjunction();
                _depth--;
                Expect(")");
                return inner;
            }

            return [Comparison()];
        }

        private KeyComparison Comparison()
        {
            switch (PeekKind)
            {
                case "begins_with":
                {
                    _at++;
                    Expect("(");
                    var attribute = Attribute();
                    Expect(",");
                    var prefix = Value();
                    Expect(")");
                    return Constrain(new KeyComparison(attribute, KeyOperator.BeginsWith, "begins_with", [prefix]));
                }

                case "value":
                {
                    var placeholder = _tokens[_at++].Text;
                    if (PeekKind is not ("cmp" or "="))
                    {
                        throw Syntax();
                    }

                    var written = _tokens[_at++].Text;
                    var operand = Resolve(placeholder);
                    var attribute = Attribute();
                    return Constrain(new KeyComparison(attribute, Mirror(Operator(written)), written, [operand]));
                }

                case "name":
                {
                    var attribute = ResolveName(_tokens[_at++].Text);
                    if (PeekKind is "cmp" or "=")
                    {
                        var written = _tokens[_at++].Text;
                        return Constrain(new KeyComparison(attribute, Operator(written), written, [Value()]));
                    }

                    if (PeekKind == "between")
                    {
                        _at++;
                        var low = Value();
                        Expect("and");
                        var high = Value();
                        return Constrain(new KeyComparison(attribute, KeyOperator.Between, "BETWEEN", [low, high]));
                    }

                    throw Syntax();
                }

                default:
                    throw Syntax();
            }
        }

        private KeyComparison Constrain(KeyComparison comparison)
        {
            if (!_constrained.Add(comparison.Attribute))
            {
                throw DynamoDbErrors.Validation("Invalid KeyConditionExpression: KeyConditionExpressions must only contain one condition per key");
            }

            var fault = OperandRefusal(comparison);
            if (fault is not null)
            {
                OperandFault = fault;
                throw fault;
            }

            return comparison;
        }

        /// <summary>
        /// The refusal of an operand the operator itself can't take (any type can be compared
        /// for equality; an operand of another type than the key's is refused once the key
        /// schema is known), or of reversed or mismatched <c>BETWEEN</c> bounds.
        /// </summary>
        private static DynamoDbException? OperandRefusal(KeyComparison comparison)
        {
            bool Allowed(DynamoValue operand) => comparison.Operator switch
            {
                KeyOperator.Equal => true,
                KeyOperator.BeginsWith => operand.Type is DynamoType.S or DynamoType.B,
                _ => operand.Type is DynamoType.S or DynamoType.N or DynamoType.B,
            };

            var bad = comparison.Operands.FirstOrDefault(o => !Allowed(o));
            if (bad is not null)
            {
                return DynamoDbErrors.Validation($"Invalid KeyConditionExpression: Incorrect operand type for operator or function; operator or function: {comparison.Written}, operand type: {bad.Type}");
            }

            if (comparison.Operator == KeyOperator.Between)
            {
                var (low, high) = (comparison.Operands[0], comparison.Operands[1]);
                if (low.Type != high.Type)
                {
                    return DynamoDbErrors.Validation($"Invalid KeyConditionExpression: The BETWEEN operator requires same data type for lower and upper bounds; lower bound operand: AttributeValue: {LegacyConditions.Brief(low)}, upper bound operand: AttributeValue: {LegacyConditions.Brief(high)}");
                }

                if (DynamoKeyOrder.Compare(low, high) > 0)
                {
                    return DynamoDbErrors.Validation($"Invalid KeyConditionExpression: The BETWEEN operator requires upper bound to be greater than or equal to lower bound; lower bound operand: AttributeValue: {LegacyConditions.Brief(low)}, upper bound operand: AttributeValue: {LegacyConditions.Brief(high)}");
                }
            }

            return null;
        }

        private static KeyOperator Operator(string text) => text switch
        {
            "=" => KeyOperator.Equal,
            "<" => KeyOperator.Less,
            "<=" => KeyOperator.LessOrEqual,
            ">" => KeyOperator.Greater,
            _ => KeyOperator.GreaterOrEqual,
        };

        private static KeyOperator Mirror(KeyOperator op) => op switch
        {
            KeyOperator.Less => KeyOperator.Greater,
            KeyOperator.LessOrEqual => KeyOperator.GreaterOrEqual,
            KeyOperator.Greater => KeyOperator.Less,
            KeyOperator.GreaterOrEqual => KeyOperator.LessOrEqual,
            _ => op,
        };

        private string Attribute()
        {
            if (PeekKind != "name")
            {
                throw Syntax();
            }

            return ResolveName(_tokens[_at++].Text);
        }

        private DynamoValue Value()
        {
            if (PeekKind != "value")
            {
                throw Syntax();
            }

            return Resolve(_tokens[_at++].Text);
        }

        private DynamoValue Resolve(string placeholder) =>
            _attributes.TryGetValue(placeholder, out var value) ? value : throw ExpressionErrors.UndefinedValue(Member, placeholder);

        /// <summary>
        /// The key attribute a name token names. A document path (<c>a.b</c>, <c>a[0]</c>) is
        /// refused as nested, after a reserved word at its head.
        /// </summary>
        private string ResolveName(string token)
        {
            var end = token.IndexOfAny(['.', '[']);
            var head = end < 0 ? token : token[..end];
            if (head.StartsWith('#'))
            {
                head = _attributes.TryGetName(head, out var name) ? name : throw ExpressionErrors.UndefinedName(Member, head);
            }
            else if (DynamoReservedWords.IsReserved(head))
            {
                if (end >= 0)
                {
                    throw ExpressionErrors.Invalid(Member, $"Attribute name is a reserved keyword; reserved keyword: {head}");
                }

                _reserved ??= head;
            }

            if (end >= 0)
            {
                throw DynamoDbErrors.Validation("Invalid KeyConditionExpression: KeyConditionExpressions cannot have conditions on nested attributes");
            }

            return head;
        }
    }
}
