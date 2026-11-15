using LocalSqsSnsMessaging.DynamoDb.Expressions;

namespace LocalSqsSnsMessaging.DynamoDb.PartiQL;

/// <summary>A projected path of a SELECT, and the attribute name it is answered under.</summary>
internal sealed record PartiQLProjection(DocumentPath Path, string Label);

/// <summary>What a PartiQL statement's WHERE clause pins: an attribute compared for equality with a value.</summary>
internal sealed record PartiQLEquality(string Attribute, DynamoValue Value);

internal abstract class PartiQLStatement
{
    public required string Table { get; init; }

    /// <summary>The WHERE clause, as a condition on an item.</summary>
    public ExpressionCondition? Where { get; init; }

    /// <summary>The top-level equalities of the WHERE clause (its AND branches comparing an attribute with a value).</summary>
    public IReadOnlyList<PartiQLEquality> Equalities { get; init; } = [];

    public abstract bool IsRead { get; }
}

internal sealed class PartiQLSelect : PartiQLStatement
{
    public string? Index { get; init; }

    /// <summary>The projected paths; null for <c>SELECT *</c>.</summary>
    public IReadOnlyList<PartiQLProjection>? Projection { get; init; }

    public (string Attribute, bool Descending)? OrderBy { get; init; }

    public override bool IsRead => true;
}

internal sealed class PartiQLInsert : PartiQLStatement
{
    public required Dictionary<string, DynamoValue> Item { get; init; }

    public override bool IsRead => false;
}

internal sealed class PartiQLUpdate : PartiQLStatement
{
    public required UpdateProgram Update { get; init; }

    /// <summary><c>ALL_OLD</c>, <c>UPDATED_OLD</c>, <c>ALL_NEW</c> or <c>UPDATED_NEW</c>, from RETURNING.</summary>
    public string? Returning { get; init; }

    public override bool IsRead => false;
}

internal sealed class PartiQLDelete : PartiQLStatement
{
    public string? Returning { get; init; }

    public override bool IsRead => false;
}

/// <summary>
/// <c>EXISTS(SELECT ...)</c>: a transaction's check that one item exists and meets the WHERE
/// clause, as TransactWriteItems' ConditionCheck. It writes nothing, and goes with the writes.
/// </summary>
internal sealed class PartiQLExists : PartiQLStatement
{
    public override bool IsRead => false;
}

/// <summary>
/// DynamoDB's PartiQL: SELECT, INSERT, UPDATE and DELETE on one table (SELECT also on an index),
/// with <c>?</c> parameters and Ion-style literals (<c>{'a': 1}</c>, <c>[1, 2]</c>,
/// <c>&lt;&lt;'a', 'b'&gt;&gt;</c>). WHERE clauses become the same conditions expressions parse to,
/// so they evaluate exactly as a <c>ConditionExpression</c> would. Refusals are worded as DynamoDB
/// words them.
/// </summary>
internal sealed class PartiQLParser
{
    private const int MaxStatementBytes = 8192;
    private const int MaxDepth = 32;

    public const string ParameterCountMessage = "Number of parameters in request and statement don't match.";
    /// <summary>The member an UPDATE's actions are applied as; its refusals are stated bare, not as an UpdateExpression's.</summary>
    public const string UpdateMember = "PartiQL";

    public const string KeyEqualityMessage = "Where clause does not contain a mandatory equality on all key attributes";

    private static readonly string[] Keywords =
    [
        "all", "and", "any", "as", "asc", "at", "bag", "between", "bigint", "blob", "bool", "boolean",
        "both", "by", "case", "cast", "char", "character", "clob", "cross", "date", "decimal",
        "delete", "desc", "distinct", "double", "else", "end", "escape", "except", "exclude", "exists",
        "extract", "false", "first", "float", "for", "from", "full", "group", "having", "in", "inner",
        "insert", "int", "int2", "int4", "int8", "integer", "intersect", "into", "is", "join", "last",
        "lateral", "left", "like", "limit", "list", "missing", "modified", "natural", "new", "not",
        "null", "nulls", "numeric", "offset", "old", "on", "or", "order", "outer", "pivot", "real",
        "remove", "replace", "returning", "right", "select", "set", "sexp", "size", "smallint", "string",
        "struct", "symbol", "table", "then", "time", "timestamp", "tinyint", "to", "true", "union",
        "unpivot", "update", "upsert", "using", "value", "values", "varchar", "when", "where", "with",
    ];

    private static readonly string[] BooleanFunctions = ["attribute_exists", "attribute_not_exists", "attribute_type", "begins_with", "contains"];
    private static readonly string[] Literals = ["null", "missing", "true", "false"];
    private static readonly string[] AttributeTypeNames = ["N", "BS", "L", "B", "NULL", "M", "S", "SS", "NS", "BOOL"];

    /// <summary>The type names <c>IS</c> reads, and the attribute type each stands for.</summary>
    private static readonly Dictionary<string, string> IsTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bool"] = "BOOL",
        ["boolean"] = "BOOL",
        ["blob"] = "B",
        ["string"] = "S",
        ["numeric"] = "N",
        ["struct"] = "M",
        ["tuple"] = "M",
        ["list"] = "L",
    };

    /// <summary>The other PartiQL type names, as PartiQL's own type classes name them in DynamoDB's refusal.</summary>
    private static readonly Dictionary<string, string> UnsupportedIsTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["smallint"] = "SmallintType",
        ["int2"] = "SmallintType",
        ["int4"] = "Integer4Type",
        ["int8"] = "Integer8Type",
        ["int"] = "IntegerType",
        ["integer"] = "IntegerType",
        ["bigint"] = "Integer8Type",
        ["float"] = "FloatType",
        ["real"] = "RealType",
        ["double"] = "DoublePrecisionType",
        ["decimal"] = "DecimalType",
        ["timestamp"] = "TimestampType",
        ["char"] = "CharacterType",
        ["character"] = "CharacterType",
        ["varchar"] = "CharacterVaryingType",
        ["symbol"] = "SymbolType",
        ["clob"] = "ClobType",
        ["date"] = "DateType",
        ["time"] = "TimeType",
        ["bag"] = "BagType",
        ["sexp"] = "SexpType",
        ["any"] = "AnyType",
    };

    public const string MissingComparedMessage = "MISSING must be compared using IS operator";

    private enum Kind
    {
        Word,
        Quoted,
        Text,
        Number,
        Parameter,
        Star,
        Compare,
        Open,
        Close,
        OpenBracket,
        CloseBracket,
        OpenBrace,
        CloseBrace,
        OpenBag,
        CloseBag,
        Comma,
        Dot,
        Colon,
        Semicolon,
        Minus,
        Plus,
        Slash,
    }

    private readonly record struct Token(Kind Kind, string Text, int Line, int Column, int Width, CompareOperator Operator = default);

    private readonly List<Token> _tokens;
    private readonly IReadOnlyList<DynamoValue> _parameters;
    private int _at;
    private int _usedParameters;
    private int _depth;
    private bool _aliased;

    private PartiQLParser(List<Token> tokens, IReadOnlyList<DynamoValue> parameters)
    {
        _tokens = tokens;
        _parameters = parameters;
    }

    private static DynamoDbException Malformed(string detail) =>
        DynamoDbErrors.Validation($"Statement wasn't well formed, can't be processed: {detail}");

    private static DynamoDbException Refused(string detail) => DynamoDbErrors.Validation($"Invalid Statement: {detail}");

    public static PartiQLStatement Parse(string statement, List<DynamoValue>? parameters)
    {
        var bound = new List<DynamoValue>();
        var oversized = false;
        foreach (var parameter in parameters ?? [])
        {
            if (parameter.Depth() > DynamoValueRules.MaxDepth)
            {
                throw DynamoDbErrors.Validation(DynamoValueRules.NestingExceeded);
            }

            oversized |= parameter.Size() > DynamoValueRules.MaxItemBytes;
            if (HoldsEmptyNumber(parameter))
            {
                // A statement parameter's empty number is refused without echoing the (empty) text.
                throw DynamoDbErrors.Validation("The parameter cannot be converted to a numeric value");
            }

            bound.Add(DynamoValueRules.Canonicalize(parameter, enveloped: false));
        }

        var parser = new PartiQLParser(Tokenize(statement), bound);
        var parsed = parser.Statement();
        if (oversized)
        {
            // A value too large for any item: an UPDATE words it as UpdateItem does, the rest as PutItem.
            throw DynamoDbErrors.Validation(parsed is PartiQLUpdate ? DynamoValueRules.UpdateItemSizeExceeded : DynamoValueRules.ItemSizeExceeded);
        }

        if (parser.Accept(Kind.Semicolon) && !parser.AtEnd)
        {
            throw Malformed("Unexpected token after semicolon. (Only one query is allowed.)");
        }

        if (!parser.AtEnd)
        {
            throw Malformed("Unexpected token after expression");
        }

        if (parser._aliased)
        {
            throw DynamoDbErrors.Validation("Aliasing is not supported");
        }

        if (parser._usedParameters != bound.Count)
        {
            throw DynamoDbErrors.Validation(ParameterCountMessage);
        }

        return parsed;
    }

    private static bool HoldsEmptyNumber(DynamoValue value) => value.Type switch
    {
        DynamoType.N => value.N.Length == 0,
        DynamoType.NS => value.NS.Any(n => n.Length == 0),
        DynamoType.L => value.L.Any(HoldsEmptyNumber),
        DynamoType.M => value.M.Values.Any(HoldsEmptyNumber),
        _ => false,
    };

    private static bool IsWordChar(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_';

    private static List<Token> Tokenize(string statement)
    {
        if (Encoding.UTF8.GetByteCount(statement) > MaxStatementBytes)
        {
            throw Refused($"a statement may hold at most {MaxStatementBytes} bytes");
        }

        var tokens = new List<Token>();
        var at = 0;
        var line = 1;
        var lineStart = 0;
        while (at < statement.Length)
        {
            var c = statement[at];
            var start = at;
            var column = start + 1 - lineStart;
            Kind? kind = null;
            var text = string.Empty;
            var op = default(CompareOperator);
            if (char.IsWhiteSpace(c))
            {
                at++;
                if (c == '\n')
                {
                    line++;
                    lineStart = at;
                }

                continue;
            }

            var following = at + 1 < statement.Length ? statement[at + 1] : '\0';
            if (c == '-' && following == '-')
            {
                // A line comment runs to the end of its line.
                while (at < statement.Length && statement[at] != '\n')
                {
                    at++;
                }

                continue;
            }

            if (c == '/' && following == '*')
            {
                var close = statement.IndexOf("*/", at + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    throw Malformed(string.Empty);
                }

                for (; at < close + 2; at++)
                {
                    if (statement[at] == '\n')
                    {
                        line++;
                        lineStart = at + 1;
                    }
                }

                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                {
                    var value = new StringBuilder();
                    var index = at + 1;
                    var closed = false;
                    while (index < statement.Length)
                    {
                        if (statement[index] == c)
                        {
                            if (index + 1 < statement.Length && statement[index + 1] == c)
                            {
                                value.Append(c);
                                index += 2;
                                continue;
                            }

                            closed = true;
                            break;
                        }

                        value.Append(statement[index++]);
                    }

                    if (!closed)
                    {
                        throw Malformed(string.Empty);
                    }

                    kind = c == '\'' ? Kind.Text : Kind.Quoted;
                    text = value.ToString();
                    at = index + 1;
                    break;
                }

                // A number may open with its decimal point (.5), but a dot after a name or a
                // closing bracket steps into a path.
                case >= '0' and <= '9':
                case '.' when char.IsDigit(following) && tokens is not [.., { Kind: Kind.Word or Kind.Quoted or Kind.CloseBracket or Kind.Close }]:
                {
                    var index = at;
                    while (index < statement.Length && char.IsDigit(statement[index]))
                    {
                        index++;
                    }

                    if (index < statement.Length && statement[index] == '.')
                    {
                        index++;
                        while (index < statement.Length && char.IsDigit(statement[index]))
                        {
                            index++;
                        }

                        if (index < statement.Length && statement[index] == '.')
                        {
                            throw Malformed(string.Empty);
                        }
                    }

                    if (index < statement.Length && statement[index] is 'e' or 'E')
                    {
                        var look = index + 1;
                        if (look < statement.Length && statement[look] is '+' or '-')
                        {
                            look++;
                        }

                        if (look >= statement.Length || !char.IsDigit(statement[look]))
                        {
                            throw Malformed(string.Empty);
                        }

                        index = look;
                        while (index < statement.Length && char.IsDigit(statement[index]))
                        {
                            index++;
                        }
                    }

                    if (index < statement.Length && IsWordChar(statement[index]))
                    {
                        throw Malformed(string.Empty);
                    }

                    kind = Kind.Number;
                    text = c == '.' ? "0" + statement[at..index] : statement[at..index];
                    at = index;
                    break;
                }

                case '?':
                    kind = Kind.Parameter;
                    at++;
                    break;
                case '*':
                    kind = Kind.Star;
                    at++;
                    break;
                case '=':
                    kind = Kind.Compare;
                    op = CompareOperator.Equal;
                    at++;
                    break;
                case '!' when at + 1 < statement.Length && statement[at + 1] == '=':
                    kind = Kind.Compare;
                    op = CompareOperator.NotEqual;
                    at += 2;
                    break;
                case '<':
                {
                    var next = at + 1 < statement.Length ? statement[at + 1] : '\0';
                    (kind, op, var width) = next switch
                    {
                        '<' => (Kind.OpenBag, default(CompareOperator), 2),
                        '=' => (Kind.Compare, CompareOperator.LessOrEqual, 2),
                        '>' => (Kind.Compare, CompareOperator.NotEqual, 2),
                        _ => (Kind.Compare, CompareOperator.Less, 1),
                    };
                    at += width;
                    break;
                }

                case '>':
                {
                    var next = at + 1 < statement.Length ? statement[at + 1] : '\0';
                    (kind, op, var width) = next switch
                    {
                        '>' => (Kind.CloseBag, default(CompareOperator), 2),
                        '=' => (Kind.Compare, CompareOperator.GreaterOrEqual, 2),
                        _ => (Kind.Compare, CompareOperator.Greater, 1),
                    };
                    at += width;
                    break;
                }

                default:
                    if (IsWordChar(c))
                    {
                        while (at < statement.Length && IsWordChar(statement[at]))
                        {
                            at++;
                        }

                        kind = Kind.Word;
                        text = statement[start..at];
                        break;
                    }

                    kind = c switch
                    {
                        '(' => Kind.Open,
                        ')' => Kind.Close,
                        '[' => Kind.OpenBracket,
                        ']' => Kind.CloseBracket,
                        '{' => Kind.OpenBrace,
                        '}' => Kind.CloseBrace,
                        ',' => Kind.Comma,
                        '.' => Kind.Dot,
                        ':' => Kind.Colon,
                        ';' => Kind.Semicolon,
                        '-' => Kind.Minus,
                        '+' => Kind.Plus,
                        '/' => Kind.Slash,

                        // A '#' is no comment marker, and a backslash escapes nothing: the
                        // statement is not well formed at all.
                        '#' or '\\' => throw Malformed(string.Empty),
                        _ => throw Malformed("Unexpected term"),
                    };
                    at++;
                    break;
            }

            tokens.Add(new Token(kind!.Value, text, line, column, at - start, op));
            for (var i = start; i < at; i++)
            {
                if (statement[i] == '\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }
        }

        if (tokens.Count == 0)
        {
            throw Malformed("Expected data manipulation");
        }

        return tokens;
    }

    private bool AtEnd => _at >= _tokens.Count;

    private Token? Peek(int offset = 0) => _at + offset < _tokens.Count ? _tokens[_at + offset] : null;

    private string Position()
    {
        if (Peek() is { } token)
        {
            return $"{token.Line}:{token.Column}:{token.Width}";
        }

        var last = _tokens[^1];
        return $"{last.Line}:{last.Column + last.Width}:0";
    }

    private static bool IsKeyword(string word) => Keywords.Any(k => string.Equals(k, word, StringComparison.OrdinalIgnoreCase));

    private DynamoDbException Unexpected() =>
        Peek() is { Kind: Kind.Word } word && IsKeyword(word.Text) ? Malformed("Unexpected keyword") : Malformed("Unexpected term");

    private bool Accept(Kind kind)
    {
        if (Peek() is { } token && token.Kind == kind)
        {
            _at++;
            return true;
        }

        return false;
    }

    private void Expect(Kind kind)
    {
        if (!Accept(kind))
        {
            throw Unexpected();
        }
    }

    private bool PeekWord(string keyword, int offset = 0) =>
        Peek(offset) is { Kind: Kind.Word } word && string.Equals(word.Text, keyword, StringComparison.OrdinalIgnoreCase);

    private bool AcceptWord(string keyword)
    {
        if (PeekWord(keyword))
        {
            _at++;
            return true;
        }

        return false;
    }

    private void ExpectWord(string keyword)
    {
        if (!AcceptWord(keyword))
        {
            throw Malformed("Expected keyword");
        }
    }

    private Token Bump()
    {
        var token = Peek() ?? throw Malformed("Unexpected term");
        _at++;
        return token;
    }

    private string Identifier()
    {
        var token = Bump();
        return token.Kind switch
        {
            Kind.Word when IsKeyword(token.Text) => throw Malformed("Unexpected keyword"),
            Kind.Word or Kind.Quoted => token.Text,
            _ => throw Malformed("Expected identifier for simple path"),
        };
    }

    private T Nested<T>(Func<T> body, int limit = MaxDepth)
    {
        if (_depth >= limit)
        {
            throw Refused($"a statement may nest at most {limit} levels deep");
        }

        _depth++;
        try
        {
            return body();
        }
        finally
        {
            _depth--;
        }
    }

    private PartiQLStatement Statement()
    {
        if (AcceptWord("select"))
        {
            return Select();
        }

        if (AcceptWord("insert"))
        {
            return Insert();
        }

        if (AcceptWord("update"))
        {
            return Update();
        }

        if (AcceptWord("delete"))
        {
            return Delete();
        }

        // FROM table WHERE ... SET ...: an UPDATE spelled with its clauses in the other order.
        if (AcceptWord("from"))
        {
            return UpdateFromFirst();
        }

        if (PeekWord("exists") && Peek(1) is { Kind: Kind.Open })
        {
            _at += 2;
            if (!AcceptWord("select"))
            {
                throw Malformed("Expected data manipulation");
            }

            var checkedItem = Select();
            Expect(Kind.Close);
            return new PartiQLExists { Table = checkedItem.Table, Where = checkedItem.Where, Equalities = checkedItem.Equalities };
        }

        foreach (var (verb, noun, operation) in new[] { ("create", "table", "CreateTable"), ("create", "index", "CreateIndex"), ("drop", "table", "DropTable"), ("drop", "index", "DropIndex") })
        {
            if (PeekWord(verb) && PeekWord(noun, 1))
            {
                throw DynamoDbErrors.Validation($"Unsupported operation: {operation}");
            }
        }

        var opened = _at;
        var read = Try(Disjunction);
        if (!read)
        {
            _at = opened;
            read = Try(Operand);

            // An operator after the expression continues it: an operand that does not follow
            // leaves no expression at all.
            while (read && (Accept(Kind.Star) || Accept(Kind.Slash) || Accept(Kind.Plus) || Accept(Kind.Minus)))
            {
                read = Try(Operand);
            }
        }

        if (!read || AtEnd || Peek()?.Kind == Kind.Semicolon)
        {
            throw Malformed("Expected data manipulation");
        }

        throw Malformed("Unexpected token after expression");
    }

    private static bool Try<T>(Func<T> parse)
    {
        try
        {
            parse();
            return true;
        }
        catch (DynamoDbException)
        {
            return false;
        }
    }

    private PartiQLSelect Select()
    {
        List<PartiQLProjection>? projection = null;
        if (!Accept(Kind.Star))
        {
            projection = [];
            do
            {
                if (Peek(1) is { Kind: Kind.Open } && Peek() is { Kind: Kind.Word })
                {
                    throw DynamoDbErrors.Validation($"Unexpected path component at {Position()}");
                }

                var path = Path();
                var label = Label(path);
                if (AcceptWord("as"))
                {
                    label = Identifier();
                }

                projection.Add(new PartiQLProjection(path, label));
            }
            while (Accept(Kind.Comma));

            var repeated = projection.GroupBy(p => p.Label, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (repeated.Count > 0)
            {
                throw DynamoDbErrors.Validation($"Duplicate identifiers in select clause: {string.Join(", ", repeated)}");
            }
        }

        if (!AcceptWord("from"))
        {
            throw Malformed("Expected FROM after SELECT list");
        }

        var table = PathComponent();
        string? index = null;
        if (Accept(Kind.Dot))
        {
            index = PathComponent();
        }

        if (Peek()?.Kind == Kind.Dot)
        {
            throw DynamoDbErrors.Validation("A path may contain at most 2 components in the FROM clause");
        }

        ReadTableAlias("where", "order", "limit");
        var where = AcceptWord("where") ? Disjunction() : null;
        (string, bool)? orderBy = null;
        if (AcceptWord("order"))
        {
            if (!AcceptWord("by"))
            {
                throw Malformed("Expected data manipulation");
            }

            var attribute = Identifier();
            var descending = AcceptWord("desc");
            if (!descending)
            {
                AcceptWord("asc");
            }

            if (Peek()?.Kind == Kind.Comma)
            {
                throw Refused("ORDER BY may name one key attribute of the source");
            }

            orderBy = (attribute, descending);
        }

        if (AcceptWord("limit"))
        {
            throw DynamoDbErrors.Validation($"Unsupported clause: LIMIT at {Position()}");
        }

        return new PartiQLSelect
        {
            Table = table,
            Index = index,
            Projection = projection,
            Where = where?.Condition,
            Equalities = where?.Equalities ?? [],
            OrderBy = orderBy,
        };
    }

    /// <summary>The name a projected path is answered under: its last name and any indexes after it.</summary>
    private static string Label(DocumentPath path)
    {
        var lastName = 0;
        for (var i = 0; i < path.Steps.Count; i++)
        {
            if (!path.Steps[i].IsIndex)
            {
                lastName = i;
            }
        }

        var label = new StringBuilder();
        for (var i = lastName; i < path.Steps.Count; i++)
        {
            var step = path.Steps[i];
            label.Append(step.IsIndex ? $"[{step.Index.ToString(CultureInfo.InvariantCulture)}]" : step.Name);
        }

        return label.ToString();
    }

    private PartiQLInsert Insert()
    {
        if (!AcceptWord("into"))
        {
            throw Malformed("Expected data manipulation");
        }

        var table = WriteTarget(position => DynamoDbErrors.Validation($"FROM clause may only contain a single table name in data manipulation statements at {position}"), "value", "values");
        if (!AcceptWord("value") && !AcceptWord("values"))
        {
            throw Unexpected();
        }

        var value = Value();
        if (value.Type != DynamoType.M)
        {
            throw Refused("the VALUE an INSERT carries must be a tuple of attribute names and values");
        }

        if (PeekWord("returning"))
        {
            throw DynamoDbErrors.Validation("RETURNING is not supported in INSERT");
        }

        return new PartiQLInsert { Table = table, Item = value.M.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal) };
    }

    private PartiQLUpdate Update()
    {
        var table = WriteTarget(_ => DynamoDbErrors.Validation("This operation is not supported on an index"), "set", "remove");
        var actions = UpdateClauses();
        var where = RequiredWhere();
        return UpdateStatement(table, actions, where);
    }

    private PartiQLUpdate UpdateFromFirst()
    {
        var table = WriteTarget(_ => DynamoDbErrors.Validation("This operation is not supported on an index"), "where");
        var where = RequiredWhere();
        var actions = UpdateClauses();
        return UpdateStatement(table, actions, where);
    }

    private List<UpdateAction> UpdateClauses()
    {
        var actions = new List<UpdateAction>();
        while (true)
        {
            // SET takes a list of assignments; REMOVE names one path.
            if (AcceptWord("set"))
            {
                do
                {
                    actions.Add(Assignment());
                }
                while (Accept(Kind.Comma));

                continue;
            }

            if (AcceptWord("remove"))
            {
                actions.Add(new RemoveAction(Path()));
                RefuseClauseList();
                continue;
            }

            break;
        }

        if (actions.Count == 0)
        {
            throw Malformed("Expected data manipulation");
        }

        return actions;
    }

    private PartiQLUpdate UpdateStatement(string table, List<UpdateAction> actions, Where where)
    {
        var returning = Returning();
        ExpressionParser.CheckPathsDisjoint("UpdateExpression", actions.Select(a => a.Path).ToList());
        return new PartiQLUpdate
        {
            Table = table,
            Update = new UpdateProgram(UpdateMember, actions),
            Where = where.Condition,
            Equalities = where.Equalities,
            Returning = returning,
        };
    }

    private PartiQLDelete Delete()
    {
        if (!AcceptWord("from"))
        {
            throw Malformed("Expected data manipulation");
        }

        var table = PathComponent();
        var index = Accept(Kind.Dot) ? PathComponent() : null;
        if (Peek()?.Kind == Kind.Dot)
        {
            throw DynamoDbErrors.Validation("A path may contain at most 2 components in the FROM clause");
        }

        ReadTableAlias("where", "returning");
        if (index is not null)
        {
            throw DynamoDbErrors.Validation("This operation is not supported on an index");
        }

        var where = RequiredWhere();
        var returning = Returning();
        if (returning is "ALL_NEW" or "UPDATED_NEW" or "UPDATED_OLD")
        {
            var clause = returning switch
            {
                "ALL_NEW" => "ALL NEW *",
                "UPDATED_NEW" => "MODIFIED NEW *",
                _ => "MODIFIED OLD *",
            };
            throw DynamoDbErrors.Validation($"Invalid returning clause: RETURNING {clause}. Only RETURNING ALL OLD * is allowed in DELETE statements.");
        }

        return new PartiQLDelete { Table = table, Where = where.Condition, Equalities = where.Equalities, Returning = returning };
    }

    private void RefuseClauseList()
    {
        if (Peek()?.Kind == Kind.Comma)
        {
            throw Malformed("Unexpected token after expression");
        }
    }

    private string PathComponent()
    {
        var component = Identifier();
        return component.Length == 0 ? throw DynamoDbErrors.Validation("Path component cannot be an empty string") : component;
    }

    private string WriteTarget(Func<string, DynamoDbException> qualified, params string[] continues)
    {
        var position = Position();
        var table = PathComponent();
        if (Peek()?.Kind == Kind.Dot)
        {
            while (Accept(Kind.Dot))
            {
                PathComponent();
            }

            throw qualified(position);
        }

        ReadTableAlias(continues);
        return table;
    }

    private void ReadTableAlias(params string[] continues)
    {
        if (AcceptWord("as"))
        {
            _aliased = true;
            if (Peek() is { Kind: Kind.Word or Kind.Quoted })
            {
                _at++;
            }

            return;
        }

        var binds = Peek() switch
        {
            { Kind: Kind.Word } word => !continues.Any(k => string.Equals(k, word.Text, StringComparison.OrdinalIgnoreCase)),
            { Kind: Kind.Quoted } => true,
            _ => false,
        };
        if (binds)
        {
            _aliased = true;
            _at++;
        }
    }

    private string? Returning()
    {
        if (!AcceptWord("returning"))
        {
            return null;
        }

        var modified = !AcceptWord("all");
        if (modified)
        {
            ExpectWord("modified");
        }

        var isNew = !AcceptWord("old");
        if (isNew)
        {
            ExpectWord("new");
        }

        Expect(Kind.Star);
        return (modified, isNew) switch
        {
            (false, false) => "ALL_OLD",
            (true, false) => "UPDATED_OLD",
            (false, true) => "ALL_NEW",
            _ => "UPDATED_NEW",
        };
    }

    private UpdateAction Assignment()
    {
        var path = Path();
        if (Peek() is not { Kind: Kind.Compare, Operator: CompareOperator.Equal })
        {
            throw Unexpected();
        }

        _at++;
        foreach (var (function, isAdd) in new[] { ("set_add", true), ("set_delete", false) })
        {
            if (PeekWord(function) && Peek(1) is { Kind: Kind.Open })
            {
                _at += 2;
                var target = FunctionPath(function);
                if (!target.Equals(path))
                {
                    throw Refused($"{function} adds to the attribute it is assigned to");
                }

                ExpectOperand(function, 1);
                var value = Value();
                ExpectCallClose(function, 2);
                return isAdd ? new AddAction(path, value) : new DeleteAction(path, value);
            }
        }

        var left = SetOperand();
        if (Peek() is { Kind: Kind.Star or Kind.Slash } unsupported)
        {
            throw DynamoDbErrors.Validation($"Unsupported operator in Update Expression. Operator: {(unsupported.Kind == Kind.Star ? "*" : "/")}");
        }

        if (Accept(Kind.Plus))
        {
            return new SetAction(path, new SetValue(left, SetOperand(), Subtract: false));
        }

        if (Accept(Kind.Minus))
        {
            return new SetAction(path, new SetValue(left, SetOperand(), Subtract: true));
        }

        return new SetAction(path, new SetValue(left, null, Subtract: false));
    }

    private SetOperand SetOperand()
    {
        if (PeekWord("list_append") && Peek(1) is { Kind: Kind.Open })
        {
            _at += 2;
            var (left, right) = Nested(() =>
            {
                var first = SetOperand();
                ExpectOperand("list_append", 1);
                return (first, SetOperand());
            });
            ExpectCallClose("list_append", 2);
            return new ListAppendOperand(left, right);
        }

        return StartsPath() ? new PathSetOperand(Path()) : new ValueSetOperand(Value());
    }

    private sealed record Where(ExpressionCondition Condition, List<PartiQLEquality> Equalities);

    private Where RequiredWhere() =>
        AcceptWord("where") ? Disjunction() : throw DynamoDbErrors.Validation(KeyEqualityMessage);

    /// <summary>A predicate, with the equalities its top-level AND branches pin.</summary>
    private Where Disjunction()
    {
        var branches = new List<Where> { Conjunction() };
        while (AcceptWord("or"))
        {
            branches.Add(Conjunction());
        }

        return branches.Count == 1 ? branches[0] : new Where(new OrCondition(branches.Select(b => b.Condition).ToList()), []);
    }

    private Where Conjunction()
    {
        var branches = new List<Where> { Negation() };
        while (AcceptWord("and"))
        {
            branches.Add(Negation());
        }

        return branches.Count == 1
            ? branches[0]
            : new Where(new AndCondition(branches.Select(b => b.Condition).ToList()), branches.SelectMany(b => b.Equalities).ToList());
    }

    private Where Negation()
    {
        if (AcceptWord("not"))
        {
            var inner = Nested(Negation);
            return new Where(new NotCondition(inner.Condition), []);
        }

        return Primary();
    }

    private Where Primary()
    {
        if (Accept(Kind.Open))
        {
            var inner = Nested(Disjunction);
            Expect(Kind.Close);
            return inner;
        }

        if (Peek(1) is { Kind: Kind.Open })
        {
            foreach (var function in BooleanFunctions)
            {
                if (!PeekWord(function))
                {
                    continue;
                }

                _at += 2;
                var path = FunctionPath(function);
                ExpressionCondition condition;
                int arity;
                switch (function)
                {
                    case "attribute_exists":
                        condition = new ExistsCondition(path, true);
                        arity = 1;
                        break;
                    case "attribute_not_exists":
                        condition = new ExistsCondition(path, false);
                        arity = 1;
                        break;
                    default:
                    {
                        ExpectOperand(function, 1);
                        var operand = Operand();
                        arity = 2;
                        if (function == "attribute_type" && operand is ValueOperand { Value: var type })
                        {
                            if (type.Type != DynamoType.S)
                            {
                                throw DynamoDbErrors.Validation($"Incorrect operand type for operator or function; operator or function: attribute_type, operand type: {type.Type}");
                            }

                            if (Array.IndexOf(AttributeTypeNames, type.S) < 0)
                            {
                                throw DynamoDbErrors.Validation($"Invalid attribute type name found; type: {type.S}, valid types: {{{string.Join(",", AttributeTypeNames)}}}");
                            }
                        }

                        var subject = new PathOperand(path);
                        condition = function switch
                        {
                            "begins_with" => new BeginsWithCondition(subject, operand),
                            "contains" => new ContainsCondition(subject, operand),
                            _ => new AttributeTypeCondition(path, operand),
                        };
                        break;
                    }
                }

                ExpectCallClose(function, arity);

                // A function's truth may be compared with a boolean literal: f(...) = true.
                if (Peek() is { Kind: Kind.Compare, Operator: CompareOperator.Equal or CompareOperator.NotEqual } truth
                    && (PeekWord("true", 1) || PeekWord("false", 1)))
                {
                    var holds = PeekWord("true", 1) == (truth.Operator == CompareOperator.Equal);
                    _at += 2;
                    return new Where(holds ? condition : new NotCondition(condition), []);
                }

                return new Where(condition, []);
            }
        }

        if (PeekWord("missing") && Peek(1) is { Kind: Kind.Compare })
        {
            throw DynamoDbErrors.Validation(MissingComparedMessage);
        }

        var predicateAt = Position();
        var left = Operand();
        if (AcceptWord("is"))
        {
            var negated = AcceptWord("not");
            if (left is not PathOperand { Path: var path })
            {
                throw Refused("IS NULL and IS MISSING read an attribute of the item");
            }

            if (AcceptWord("null"))
            {
                ExpressionCondition isNull = new CompareCondition(CompareOperator.Equal, left, new ValueOperand(DynamoValue.NullValue));
                return new Where(negated ? new NotCondition(isNull) : isNull, []);
            }

            if (Peek() is { Kind: Kind.Word } typeName && !string.Equals(typeName.Text, "missing", StringComparison.OrdinalIgnoreCase))
            {
                if (IsTypeNames.TryGetValue(typeName.Text, out var attributeType))
                {
                    _at++;
                    ExpressionCondition isType = new AttributeTypeCondition(path, new ValueOperand(DynamoValue.FromString(attributeType)));
                    return new Where(negated ? new NotCondition(isType) : isType, []);
                }

                if (UnsupportedIsTypeNames.TryGetValue(typeName.Text, out var label))
                {
                    throw DynamoDbErrors.Validation(
                        $"Unsupported data type for IS operator: {label}. Supported data types are [MISSING, NULL, BOOLEAN, BLOB, STRING, NUMERIC, STRUCT, TUPLE, LIST] at {predicateAt}");
                }
            }

            ExpectWord("missing");
            return new Where(new ExistsCondition(path, negated), []);
        }

        var notted = AcceptWord("not");
        if (AcceptWord("between"))
        {
            var low = Operand();
            ExpectWord("and");
            var high = Operand();
            CheckScalarOperands("BETWEEN", left, low, high);
            ExpressionCondition between = new BetweenCondition(left, low, high);
            return new Where(notted ? new NotCondition(between) : between, []);
        }

        if (AcceptWord("in"))
        {
            var closing = Kind.Close;
            if (!Accept(Kind.Open))
            {
                Expect(Kind.OpenBracket);
                closing = Kind.CloseBracket;
            }

            var candidates = new List<Operand>();
            do
            {
                candidates.Add(Operand());
            }
            while (Accept(Kind.Comma));

            Expect(closing);
            ExpressionCondition membership = new InCondition(left, candidates);
            return new Where(notted ? new NotCondition(membership) : membership, []);
        }

        if (notted || Peek() is not { Kind: Kind.Compare } compare)
        {
            throw Unexpected();
        }

        _at++;
        if (PeekWord("missing"))
        {
            throw DynamoDbErrors.Validation(MissingComparedMessage);
        }

        var right = Operand();
        if (compare.Operator is not (CompareOperator.Equal or CompareOperator.NotEqual))
        {
            CheckScalarOperands(compare.Operator.Text(), left, right);
        }

        var equalities = new List<PartiQLEquality>();
        if (compare.Operator == CompareOperator.Equal)
        {
            if (left is PathOperand { Path.IsTopLevel: true } l && right is ValueOperand r)
            {
                equalities.Add(new PartiQLEquality(l.Path.AttributeName, r.Value));
            }
            else if (right is PathOperand { Path.IsTopLevel: true } rp && left is ValueOperand lv)
            {
                equalities.Add(new PartiQLEquality(rp.Path.AttributeName, lv.Value));
            }
        }

        return new Where(new CompareCondition(compare.Operator, left, right), equalities);
    }

    private static void CheckScalarOperands(string op, params Operand[] operands)
    {
        foreach (var operand in operands)
        {
            if (operand is ValueOperand { Value: var value } && value.Type is not (DynamoType.S or DynamoType.N or DynamoType.B))
            {
                throw DynamoDbErrors.Validation($"Incorrect operand type for operator or function; operator or function: {op}, operand type: {value.Type}");
            }
        }
    }

    private DocumentPath FunctionPath(string function)
    {
        if (Peek()?.Kind == Kind.Close)
        {
            throw IncorrectOperandCount(function, 0);
        }

        if (!StartsPath())
        {
            throw DynamoDbErrors.Validation($"Operator or function requires a document path; operator or function: {function}");
        }

        return Path();
    }

    private static DynamoDbException IncorrectOperandCount(string function, int operands) =>
        DynamoDbErrors.Validation($"Incorrect number of operands for operator or function; operator or function: {function}, number of operands: {operands.ToString(CultureInfo.InvariantCulture)}");

    private void ExpectOperand(string function, int read)
    {
        if (!Accept(Kind.Comma))
        {
            throw IncorrectOperandCount(function, OperandsInCall(read));
        }
    }

    private void ExpectCallClose(string function, int read)
    {
        if (!Accept(Kind.Close))
        {
            throw IncorrectOperandCount(function, OperandsInCall(read));
        }
    }

    private int OperandsInCall(int read)
    {
        var count = read;
        var depth = 0;
        for (var i = _at; i < _tokens.Count; i++)
        {
            switch (_tokens[i].Kind)
            {
                case Kind.Open or Kind.OpenBracket or Kind.OpenBrace or Kind.OpenBag:
                    depth++;
                    break;
                case Kind.Close when depth == 0:
                    return count;
                case Kind.Close or Kind.CloseBracket or Kind.CloseBrace or Kind.CloseBag:
                    depth = Math.Max(0, depth - 1);
                    break;
                case Kind.Comma when depth == 0:
                    count++;
                    break;
            }
        }

        return count;
    }

    private Operand Operand()
    {
        if (PeekWord("size") && Peek(1) is { Kind: Kind.Open })
        {
            _at += 2;
            var path = FunctionPath("size");
            ExpectCallClose("size", 1);
            return new SizeOperand(path);
        }

        return StartsPath() ? new PathOperand(Path()) : new ValueOperand(Value());
    }

    private bool StartsPath() => Peek() switch
    {
        { Kind: Kind.Quoted } => true,
        { Kind: Kind.Word } word => !Literals.Any(l => string.Equals(l, word.Text, StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };

    private static bool IsDefinedFunction(string name) =>
        BooleanFunctions.Concat(["size", "list_append", "set_add", "set_delete"]).Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));

    private DocumentPath Path()
    {
        if (Peek(1) is { Kind: Kind.Open } && Peek() is { Kind: Kind.Word } function)
        {
            throw IsDefinedFunction(function.Text)
                ? Malformed("Expected identifier for simple path")
                : DynamoDbErrors.Validation($"Unrecognized function: {function.Text} at {Position()}");
        }

        var steps = new List<PathStep> { PathStep.Attribute(Identifier()) };
        while (true)
        {
            if (steps.Count > MaxDepth)
            {
                throw Refused($"a document path may hold at most {MaxDepth} steps");
            }

            if (Accept(Kind.Dot))
            {
                steps.Add(PathStep.Attribute(Identifier()));
                continue;
            }

            if (Accept(Kind.OpenBracket))
            {
                if (Peek() is { Kind: Kind.Minus } && Peek(1) is { Kind: Kind.Number } negative)
                {
                    _at++;
                    throw DynamoDbErrors.Validation($"List index is not within the allowable range; index: [-{negative.Text}] at {Position()}");
                }

                var token = Bump();
                if (token.Kind != Kind.Number || !int.TryParse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    throw Malformed("Unexpected term");
                }

                Expect(Kind.CloseBracket);
                steps.Add(PathStep.Element(index));
                continue;
            }

            break;
        }

        return new DocumentPath(steps);
    }

    private DynamoValue Value()
    {
        var value = RawValue();
        if (value.Depth() > DynamoValueRules.MaxDepth)
        {
            throw DynamoDbErrors.Validation(DynamoValueRules.NestingExceeded);
        }

        return value;
    }

    private DynamoValue RawValue()
    {
        var token = Peek() ?? throw Malformed("Unexpected term");
        switch (token.Kind)
        {
            case Kind.Text:
                _at++;
                return DynamoValue.FromString(token.Text);
            case Kind.Number:
                _at++;
                return DynamoValue.FromNumber(DynamoValueRules.ParseNumber(token.Text, enveloped: false));
            case Kind.Minus or Kind.Plus:
            {
                _at++;
                if (Peek() is not { Kind: Kind.Number } number)
                {
                    throw Malformed("Unexpected term");
                }

                _at++;
                return DynamoValue.FromNumber(DynamoValueRules.ParseNumber((token.Kind == Kind.Minus ? "-" : "") + number.Text, enveloped: false));
            }

            case Kind.Parameter:
                _at++;
                if (_usedParameters >= _parameters.Count)
                {
                    throw DynamoDbErrors.Validation(ParameterCountMessage);
                }

                return _parameters[_usedParameters++];
            case Kind.OpenBracket:
                _at++;
                return DynamoValue.FromList(Nested(() => ValueList(Kind.CloseBracket), DynamoValueRules.MaxDepth));
            case Kind.OpenBag:
                _at++;
                return SetOf(Nested(() => ValueList(Kind.CloseBag), DynamoValueRules.MaxDepth));
            case Kind.OpenBrace:
                _at++;
                return DynamoValue.FromMap(Nested(TupleEntries, DynamoValueRules.MaxDepth));
            case Kind.Word when string.Equals(token.Text, "null", StringComparison.OrdinalIgnoreCase):
                _at++;
                return DynamoValue.NullValue;
            case Kind.Word when string.Equals(token.Text, "true", StringComparison.OrdinalIgnoreCase):
                _at++;
                return DynamoValue.True;
            case Kind.Word when string.Equals(token.Text, "false", StringComparison.OrdinalIgnoreCase):
                _at++;
                return DynamoValue.False;
            case Kind.Word when !IsKeyword(token.Text):
                throw DynamoDbErrors.Validation($"Unsupported data type: {token.Text} under key root at {Position()}");
            default:
                throw Unexpected();
        }
    }

    private List<DynamoValue> ValueList(Kind closing)
    {
        var members = new List<DynamoValue>();
        if (Accept(closing))
        {
            return members;
        }

        do
        {
            members.Add(RawValue());
        }
        while (Accept(Kind.Comma));

        ExpectClose(closing);
        return members;
    }

    private void ExpectClose(Kind closing)
    {
        if (!Accept(closing))
        {
            throw Malformed(closing switch
            {
                Kind.CloseBrace => "Expected RIGHT_CURLY",
                Kind.CloseBracket => "Expected RIGHT_BRACKET",
                Kind.CloseBag => "Expected RIGHT_DOUBLE_ANGLE_BRACKET",
                _ => "Unexpected term",
            });
        }
    }

    private Dictionary<string, DynamoValue> TupleEntries()
    {
        var entries = new Dictionary<string, DynamoValue>(StringComparer.Ordinal);
        if (Accept(Kind.CloseBrace))
        {
            return entries;
        }

        do
        {
            var token = Peek();
            if (token is not { Kind: Kind.Text or Kind.Quoted } name)
            {
                throw Unexpected();
            }

            _at++;
            Expect(Kind.Colon);
            var value = RawValue();
            if (!entries.TryAdd(name.Text, value))
            {
                throw Refused("a tuple names each of its attributes once");
            }
        }
        while (Accept(Kind.Comma));

        ExpectClose(Kind.CloseBrace);
        return entries;
    }

    private static DynamoValue SetOf(List<DynamoValue> members)
    {
        if (members.Count == 0)
        {
            throw Refused("a set must hold at least one member");
        }

        if (members.TrueForAll(m => m.Type == DynamoType.S))
        {
            return DynamoValueRules.Canonicalize(DynamoValue.FromStringSet(members.Select(m => m.S).ToList()), enveloped: false);
        }

        if (members.TrueForAll(m => m.Type == DynamoType.N))
        {
            return DynamoValueRules.Canonicalize(DynamoValue.FromNumberSet(members.Select(m => m.N).ToList()), enveloped: false);
        }

        if (members.TrueForAll(m => m.Type == DynamoType.B))
        {
            return DynamoValueRules.Canonicalize(DynamoValue.FromBinarySet(members.Select(m => m.B).ToList()), enveloped: false);
        }

        throw DynamoDbErrors.Validation("Unsupported data type in Bag. DynamoDB only supports either numbers or strings in bags");
    }
}
