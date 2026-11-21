namespace InMemoryAws.DynamoDb.Expressions;

internal enum TokenKind
{
    Word,
    Alias,
    Placeholder,
    Index,
    Compare,
    Open,
    Close,
    Comma,
    Dot,
    Plus,
    Minus,
}

internal enum CompareOperator
{
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}

/// <summary>One token of an expression, with where it was in the text.</summary>
internal readonly record struct ExpressionToken(TokenKind Kind, string Text, int Start, int End, CompareOperator Operator = default, int Index = 0);

/// <summary>How DynamoDB words the refusals of an expression member.</summary>
internal static class ExpressionErrors
{
    public const int MaxExpressionBytes = 4096;
    public const int MaxTokens = 1024;
    public const int MaxDepth = 16;
    public const int MaxInOperands = 100;
    public const int MaxOperators = 300;

    /// <summary>
    /// The members whose refusals DynamoDB states inside the validation envelope; the others
    /// (projections, filters, key conditions) it states bare.
    /// </summary>
    public static bool IsEnveloped(string member) => member is "UpdateExpression" or "ConditionExpression";

    public static DynamoDbException Refusal(string member, string clause) =>
        IsEnveloped(member) ? DynamoDbErrors.ValidationClause(clause) : DynamoDbErrors.Validation(clause);

    public static DynamoDbException Invalid(string member, string detail) => Refusal(member, $"Invalid {member}: {detail}");

    public static DynamoDbException UndefinedName(string member, string alias) =>
        Invalid(member, $"An expression attribute name used in the document path is not defined; attribute name: {alias}");

    public static DynamoDbException UndefinedValue(string member, string placeholder) =>
        Invalid(member, $"An expression attribute value used in expression is not defined; attribute value: {placeholder}");

    public static DynamoDbException Syntax(string member, string token, string near) =>
        Invalid(member, $"Syntax error; token: \"{token}\", near: \"{near}\"");

    public static DynamoDbException IncorrectOperand(string member, string operatorOrFunction, DynamoType type) =>
        Invalid(member, $"Incorrect operand type for operator or function; operator or function: {operatorOrFunction}, operand type: {type}");

    /// <summary>A character the tokenizer can't read: the character, and the words around it.</summary>
    public static DynamoDbException Lexical(string member, string text, int at)
    {
        at = Math.Min(at, text.Length);
        var token = at < text.Length ? text[at].ToString() : "<EOF>";
        var runStart = at;
        while (runStart > 0 && !char.IsWhiteSpace(text[runStart - 1]))
        {
            runStart--;
        }

        var end = at;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        var current = text[runStart..Math.Max(end, runStart)];
        var last = runStart - 1;
        while (last >= 0 && char.IsWhiteSpace(text[last]))
        {
            last--;
        }

        string near;
        if (last >= 0)
        {
            var start = last;
            while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
            {
                start--;
            }

            near = text[start..(last + 1)] + " " + current;
        }
        else
        {
            near = current;
        }

        return Syntax(member, token, near);
    }

    public static string Text(this CompareOperator op) => op switch
    {
        CompareOperator.Equal => "=",
        CompareOperator.NotEqual => "<>",
        CompareOperator.Less => "<",
        CompareOperator.LessOrEqual => "<=",
        CompareOperator.Greater => ">",
        _ => ">=",
    };
}

/// <summary>Splits an expression into tokens, as DynamoDB reads them.</summary>
internal static class ExpressionTokenizer
{
    private static bool IsWordChar(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_';

    public static List<ExpressionToken> Tokenize(string member, string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > ExpressionErrors.MaxExpressionBytes)
        {
            throw ExpressionErrors.Invalid(member, "Expression size has exceeded the maximum allowed size;");
        }

        var tokens = new List<ExpressionToken>();
        var at = 0;
        while (at < text.Length)
        {
            if (tokens.Count >= ExpressionErrors.MaxTokens)
            {
                throw DynamoDbErrors.Validation($"Invalid {member}: an expression may hold at most {ExpressionErrors.MaxTokens} tokens");
            }

            var c = text[at];
            var start = at;
            if (char.IsWhiteSpace(c))
            {
                at++;
                continue;
            }

            switch (c)
            {
                case '(':
                    tokens.Add(new(TokenKind.Open, "(", start, ++at));
                    continue;
                case ')':
                    tokens.Add(new(TokenKind.Close, ")", start, ++at));
                    continue;
                case ',':
                    tokens.Add(new(TokenKind.Comma, ",", start, ++at));
                    continue;
                case '.':
                    tokens.Add(new(TokenKind.Dot, ".", start, ++at));
                    continue;
                case '+':
                    tokens.Add(new(TokenKind.Plus, "+", start, ++at));
                    continue;
                case '-':
                    tokens.Add(new(TokenKind.Minus, "-", start, ++at));
                    continue;
                case '=':
                    tokens.Add(new(TokenKind.Compare, "=", start, ++at, CompareOperator.Equal));
                    continue;
                case '<' or '>':
                {
                    var next = at + 1 < text.Length ? text[at + 1] : '\0';
                    var (op, width) = (c, next) switch
                    {
                        ('<', '=') => (CompareOperator.LessOrEqual, 2),
                        ('<', '>') => (CompareOperator.NotEqual, 2),
                        ('<', _) => (CompareOperator.Less, 1),
                        (_, '=') => (CompareOperator.GreaterOrEqual, 2),
                        _ => (CompareOperator.Greater, 1),
                    };
                    at += width;
                    tokens.Add(new(TokenKind.Compare, text[start..at], start, at, op));
                    continue;
                }

                case '[':
                {
                    if (at + 1 < text.Length && text[at + 1] == '-')
                    {
                        // A negative index: DynamoDB's grammar reads '[' and then fails on the '-',
                        // naming the tokens around it ('[', '-' and the number after it).
                        var digits = at + 2;
                        while (digits < text.Length && text[digits] is >= '0' and <= '9')
                        {
                            digits++;
                        }

                        throw ExpressionErrors.Syntax(member, "-", text[at..digits]);
                    }

                    var end = at + 1;
                    while (end < text.Length && text[end] is >= '0' and <= '9')
                    {
                        end++;
                    }

                    if (end == at + 1 || end >= text.Length || text[end] != ']'
                        || !int.TryParse(text[(at + 1)..end], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    {
                        throw ExpressionErrors.Lexical(member, text, at);
                    }

                    at = end + 1;
                    tokens.Add(new(TokenKind.Index, text[start..at], start, at, Index: index));
                    continue;
                }

                case '#' or ':':
                {
                    at++;
                    while (at < text.Length && IsWordChar(text[at]))
                    {
                        at++;
                    }

                    if (at - start < 2)
                    {
                        throw ExpressionErrors.Lexical(member, text, start);
                    }

                    tokens.Add(new(c == '#' ? TokenKind.Alias : TokenKind.Placeholder, text[start..at], start, at));
                    continue;
                }
            }

            if (IsWordChar(c))
            {
                while (at < text.Length && IsWordChar(text[at]))
                {
                    at++;
                }

                tokens.Add(new(TokenKind.Word, text[start..at], start, at));
                continue;
            }

            throw ExpressionErrors.Lexical(member, text, at);
        }

        if (tokens.Count == 0)
        {
            throw ExpressionErrors.Invalid(member, "The expression can not be empty;");
        }

        return tokens;
    }
}

/// <summary>
/// A request's <c>ExpressionAttributeNames</c> and <c>ExpressionAttributeValues</c>, which its
/// expressions refer to by <c>#name</c> and <c>:value</c>.
/// </summary>
internal sealed class ExpressionAttributes(Dictionary<string, string>? names, Dictionary<string, DynamoValue>? values)
{
    public Dictionary<string, string>? Names { get; } = names;

    public Dictionary<string, DynamoValue>? Values { get; } = values;

    public bool TryGetName(string alias, out string name)
    {
        name = string.Empty;
        return Names is not null && Names.TryGetValue(alias, out name!);
    }

    public bool TryGetValue(string placeholder, out DynamoValue value)
    {
        value = null!;
        return Values is not null && Values.TryGetValue(placeholder, out value!);
    }

    /// <summary>
    /// What DynamoDB checks of the names and values before it reads any expression: that they're
    /// sent only with expressions that can use them, that neither table is empty, that every
    /// <c>#name</c> and <c>:value</c> an expression uses is defined, and that every one defined is used.
    /// </summary>
    public void CheckAllReferenced(params (string Member, string? Expression)[] expressions)
    {
        var enveloped = expressions.Any(e => ExpressionErrors.IsEnveloped(e.Member));
        if (Names is { Count: > 0 } && !expressions.Any(e => e.Expression is not null))
        {
            throw Refusal(enveloped, "ExpressionAttributeNames can only be specified when using expressions");
        }

        if (Values is { Count: > 0 } && !expressions.Any(e => e.Expression is not null && e.Member != "ProjectionExpression"))
        {
            throw Refusal(enveloped, "ExpressionAttributeValues can only be specified when using expressions");
        }

        if (Names is { Count: 0 })
        {
            throw Refusal(enveloped, "ExpressionAttributeNames must not be empty");
        }

        if (Values is { Count: 0 })
        {
            throw Refusal(enveloped, "ExpressionAttributeValues must not be empty");
        }

        CheckKeys("ExpressionAttributeNames", Names?.Keys, '#', enveloped);
        CheckKeys("ExpressionAttributeValues", Values?.Keys, ':', enveloped);

        var emptyName = Names?.Where(n => n.Value.Length == 0).Select(n => n.Key).OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
        if (emptyName is not null)
        {
            throw Refusal(enveloped, $"ExpressionAttributeNames contains invalid value: Empty attribute name for key {emptyName}");
        }

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var usedValues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (member, expression) in expressions)
        {
            if (expression is null)
            {
                continue;
            }

            List<ExpressionToken> tokens;
            try
            {
                tokens = ExpressionTokenizer.Tokenize(member, expression);
            }
            catch (DynamoDbException)
            {
                // The expression's own refusal comes when it is parsed.
                return;
            }

            foreach (var token in tokens)
            {
                if (token.Kind == TokenKind.Alias)
                {
                    if (!TryGetName(token.Text, out _))
                    {
                        throw ExpressionErrors.UndefinedName(member, token.Text);
                    }

                    usedNames.Add(token.Text);
                }
                else if (token.Kind == TokenKind.Placeholder)
                {
                    if (!TryGetValue(token.Text, out _))
                    {
                        throw ExpressionErrors.UndefinedValue(member, token.Text);
                    }

                    usedValues.Add(token.Text);
                }
            }
        }

        var enveloping = expressions.Any(e => e.Expression is not null && ExpressionErrors.IsEnveloped(e.Member));
        CheckUnused("ExpressionAttributeNames", Names?.Keys, usedNames, enveloping);
        CheckUnused("ExpressionAttributeValues", Values?.Keys, usedValues, enveloping);
    }

    /// <summary>Refuses a key that is too long, or isn't its prefix followed by letters, digits and underscores.</summary>
    private static void CheckKeys(string member, IEnumerable<string>? keys, char prefix, bool enveloped)
    {
        foreach (var key in keys?.OrderBy(k => k, StringComparer.Ordinal) ?? Enumerable.Empty<string>())
        {
            var bytes = Encoding.UTF8.GetByteCount(key);
            if (bytes > MaxKeyBytes)
            {
                throw Refusal(enveloped, $"{member} contains invalid key: The expression attribute map contains a key that is too long; size of key: {bytes.ToString(CultureInfo.InvariantCulture)}");
            }

            if (key.Length < 2 || key[0] != prefix || !key.Skip(1).All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_'))
            {
                throw Refusal(enveloped, $"{member} contains invalid key: Syntax error; key: \"{key}\"");
            }
        }
    }

    private const int MaxKeyBytes = 255;

    private static DynamoDbException Refusal(bool enveloped, string clause) =>
        enveloped ? DynamoDbErrors.ValidationClause(clause) : DynamoDbErrors.Validation(clause);

    private static void CheckUnused(string member, IEnumerable<string>? defined, HashSet<string> used, bool enveloped)
    {
        var unused = defined?.Where(k => !used.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (unused is { Count: > 0 })
        {
            throw Refusal(enveloped, $"Value provided in {member} unused in expressions: keys: {{{string.Join(", ", unused)}}}");
        }
    }
}

/// <summary>
/// The parser for condition, filter, projection and update expressions: recursive descent over the
/// tokens, with DynamoDB's precedence (comparisons, then <c>IN</c>, <c>BETWEEN</c> and functions,
/// then <c>NOT</c>, <c>AND</c>, <c>OR</c>). A reserved word used as a name and redundant
/// parentheses are refused only once the whole expression has parsed, as DynamoDB refuses them.
/// </summary>
internal sealed class ExpressionParser
{
    private readonly string _member;
    private readonly string _text;
    private readonly List<ExpressionToken> _tokens;
    private readonly ExpressionAttributes _attributes;
    private int _at;
    private int _depth;
    private int _operators;
    private string? _reserved;

    private ExpressionParser(string member, string text, ExpressionAttributes attributes)
    {
        _member = member;
        _text = text;
        _attributes = attributes;
        _tokens = ExpressionTokenizer.Tokenize(member, text);
    }

    public static ExpressionCondition ParseCondition(string member, string text, ExpressionAttributes attributes)
    {
        var parser = new ExpressionParser(member, text, attributes);
        var condition = parser.Disjunction();
        if (!parser.AtEnd)
        {
            throw parser.SyntaxError();
        }

        parser.CheckOperatorCount();
        parser.CheckNoReservedName();
        parser.CheckNoRedundantParentheses();
        return condition;
    }

    public static List<DocumentPath> ParseProjection(string text, ExpressionAttributes attributes)
    {
        const string member = "ProjectionExpression";
        var parser = new ExpressionParser(member, text, attributes);
        var paths = new List<DocumentPath> { parser.Path() };
        while (parser.Eat(TokenKind.Comma))
        {
            paths.Add(parser.Path());
        }

        if (!parser.AtEnd)
        {
            throw parser.SyntaxError();
        }

        parser.CheckNoReservedName();
        CheckPathsDisjoint(member, paths);
        return paths;
    }

    public static UpdateProgram ParseUpdate(string text, ExpressionAttributes attributes)
    {
        const string member = "UpdateExpression";
        var parser = new ExpressionParser(member, text, attributes);
        var actions = parser.UpdateActions();
        parser.CheckOperatorCount();
        parser.CheckNoReservedName();
        parser.CheckNoRedundantParentheses();
        CheckPathsDisjoint(member, actions.Select(a => a.Path).ToList());
        return new UpdateProgram(member, actions);
    }

    /// <summary>Refuses two paths of which one is, or starts with, the other.</summary>
    public static void CheckPathsDisjoint(string member, IReadOnlyList<DocumentPath> paths)
    {
        // Compared by step, not as rendered text: an attribute named "a.b" (through an alias) is not the path a.b.
        var stated = new Dictionary<DocumentPath, int>();
        for (var i = 0; i < paths.Count; i++)
        {
            if (stated.ContainsKey(paths[i]))
            {
                throw Overlap(member, paths[i].RenderTokens(), paths[i].RenderTokens());
            }

            stated[paths[i]] = i;
        }

        for (var i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            for (var steps = 1; steps < path.Steps.Count; steps++)
            {
                if (stated.TryGetValue(new DocumentPath(path.Steps.Take(steps).ToList()), out var earlier))
                {
                    var whole = path.RenderTokens();
                    var prefix = path.RenderTokens(steps);
                    throw earlier < i ? Overlap(member, prefix, whole) : Overlap(member, whole, prefix);
                }
            }
        }
    }

    private static DynamoDbException Overlap(string member, string one, string two) =>
        ExpressionErrors.Invalid(member, $"Two document paths overlap with each other; must remove or rewrite one of these paths; path one: {one}, path two: {two}");

    /// <summary>The words the expression grammar reads as operators, never as an attribute name.</summary>
    private static readonly string[] GrammarKeywords = ["AND", "OR", "NOT", "BETWEEN", "IN"];

    private bool AtEnd => _at >= _tokens.Count;

    private ExpressionToken? Peek(int offset = 0) => _at + offset < _tokens.Count ? _tokens[_at + offset] : null;

    private DynamoDbException SyntaxError()
    {
        var token = AtEnd ? "<EOF>" : _tokens[_at].Text;
        var last = _tokens.Count - 1;
        var first = Math.Min(Math.Max(_at - 1, 0), last);
        var end = Math.Min(_at + 1, last);
        var near = _text[_tokens[first].Start..Math.Max(_tokens[end].End, _tokens[first].Start)];
        return ExpressionErrors.Syntax(_member, token, near);
    }

    /// <summary>Refuses an expression with more than 300 operators and functions; an IN counts once.</summary>
    private void CheckOperatorCount()
    {
        if (_operators > ExpressionErrors.MaxOperators)
        {
            throw ExpressionErrors.Invalid(_member, $"The expression contains too many operators; operator count: {_operators.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private void CheckNoReservedName()
    {
        if (_reserved is not null)
        {
            throw ExpressionErrors.Invalid(_member, $"Attribute name is a reserved keyword; reserved keyword: {_reserved}");
        }
    }

    private void CheckNoRedundantParentheses()
    {
        var openedBy = new int[_tokens.Count];
        var open = new Stack<int>();
        for (var i = 0; i < _tokens.Count; i++)
        {
            openedBy[i] = -1;
            if (_tokens[i].Kind == TokenKind.Open)
            {
                open.Push(i);
            }
            else if (_tokens[i].Kind == TokenKind.Close)
            {
                if (open.Count == 0)
                {
                    return;
                }

                var opener = open.Pop();
                openedBy[i] = opener;
                if (i > 0 && _tokens[i - 1].Kind == TokenKind.Close && openedBy[i - 1] == opener + 1)
                {
                    throw ExpressionErrors.Invalid(_member, "The expression has redundant parentheses;");
                }
            }
        }
    }

    private bool Eat(TokenKind kind)
    {
        if (Peek() is { } token && token.Kind == kind)
        {
            _at++;
            return true;
        }

        return false;
    }

    private void Expect(TokenKind kind)
    {
        if (!Eat(kind))
        {
            throw SyntaxError();
        }
    }

    private bool EatKeyword(string keyword)
    {
        if (Peek() is { Kind: TokenKind.Word } token && string.Equals(token.Text, keyword, StringComparison.OrdinalIgnoreCase))
        {
            _at++;
            return true;
        }

        return false;
    }

    private ExpressionToken Next()
    {
        var token = Peek() ?? throw SyntaxError();
        _at++;
        return token;
    }

    private string? PeekCall() =>
        Peek() is { Kind: TokenKind.Word } word && Peek(1) is { Kind: TokenKind.Open } ? word.Text : null;

    private void Descend()
    {
        if (++_depth > ExpressionErrors.MaxDepth)
        {
            throw DynamoDbErrors.Validation($"Invalid {_member}: an expression may nest at most {ExpressionErrors.MaxDepth} levels deep");
        }
    }

    private string ResolveAlias(string alias) =>
        _attributes.TryGetName(alias, out var name) ? name : throw ExpressionErrors.UndefinedName(_member, alias);

    private DynamoValue ResolvePlaceholder(string placeholder) =>
        _attributes.TryGetValue(placeholder, out var value) ? value : throw ExpressionErrors.UndefinedValue(_member, placeholder);

    private DocumentPath Path()
    {
        var steps = new List<PathStep> { PathStep.Attribute(PathName()) };
        while (true)
        {
            // A path may name as many levels as an item may nest, and no more.
            if (steps.Count >= DynamoValueRules.MaxDepth && Peek() is { Kind: TokenKind.Dot or TokenKind.Index })
            {
                throw DynamoDbErrors.Validation($"Invalid {_member}: a document path may name at most {DynamoValueRules.MaxDepth} levels");
            }

            if (Peek() is { Kind: TokenKind.Dot })
            {
                _at++;
                steps.Add(PathStep.Attribute(PathName()));
            }
            else if (Peek() is { Kind: TokenKind.Index } index)
            {
                _at++;
                steps.Add(PathStep.Element(index.Index));
            }
            else
            {
                break;
            }
        }

        return new DocumentPath(steps);
    }

    private string PathName()
    {
        // A name can't start with a digit, nor be one of the grammar's own keywords: `a = :v AND
        // AND b = :v` fails at the second AND, not as a reserved attribute name.
        if (Peek() is { Kind: TokenKind.Word } word
            && (word.Text[0] is >= '0' and <= '9' || GrammarKeywords.Contains(word.Text, StringComparer.OrdinalIgnoreCase)))
        {
            throw SyntaxError();
        }

        var token = Next();
        switch (token.Kind)
        {
            case TokenKind.Word:
                if (_reserved is null && DynamoReservedWords.IsReserved(token.Text))
                {
                    _reserved = token.Text;
                }

                return token.Text;
            case TokenKind.Alias:
                return ResolveAlias(token.Text);
            case TokenKind.Open:
            {
                // DynamoDB's grammar reads an opening parenthesis where it expects an operand, so a
                // defect after it is reported at the token that follows: attribute_exists(( fails
                // at <EOF>. A parenthesized path stays refused at its parenthesis.
                var open = _at - 1;
                while (Peek() is { Kind: TokenKind.Open })
                {
                    _at++;
                }

                if (Peek() is { Kind: TokenKind.Word or TokenKind.Alias })
                {
                    _at = open;
                }

                throw SyntaxError();
            }

            default:
                _at--;
                throw SyntaxError();
        }
    }

    // Conditions

    private ExpressionCondition Disjunction()
    {
        var nodes = new List<ExpressionCondition> { Conjunction() };
        while (EatKeyword("OR"))
        {
            _operators++;
            nodes.Add(Conjunction());
        }

        return nodes.Count == 1 ? nodes[0] : new OrCondition(nodes);
    }

    private ExpressionCondition Conjunction()
    {
        var nodes = new List<ExpressionCondition> { Negation() };
        while (EatKeyword("AND"))
        {
            _operators++;
            nodes.Add(Negation());
        }

        return nodes.Count == 1 ? nodes[0] : new AndCondition(nodes);
    }

    private ExpressionCondition Negation()
    {
        if (EatKeyword("NOT"))
        {
            _operators++;
            Descend();
            var inner = Negation();
            _depth--;
            return new NotCondition(inner);
        }

        return Primary();
    }

    private ExpressionCondition Primary()
    {
        var grouped = Peek() is { Kind: TokenKind.Open } ? GroupedOperand() : null;
        if (grouped is null && Eat(TokenKind.Open))
        {
            Descend();
            var node = Disjunction();
            _depth--;
            Expect(TokenKind.Close);
            return node;
        }

        if (grouped is null && PeekCall() is { } name && !string.Equals(name, "size", StringComparison.Ordinal))
        {
            return Function(name);
        }

        var left = grouped ?? Operand();
        _operators++;
        if (EatKeyword("BETWEEN"))
        {
            var low = Operand();
            if (!EatKeyword("AND"))
            {
                throw SyntaxError();
            }

            var high = Operand();
            CheckOrderable("BETWEEN", left);
            CheckOrderable("BETWEEN", low);
            CheckOrderable("BETWEEN", high);
            if (low is ValueOperand { Value: var lower } && high is ValueOperand { Value: var upper })
            {
                var bounds = $"lower bound operand: AttributeValue: {LegacyConditions.Brief(lower)}, upper bound operand: AttributeValue: {LegacyConditions.Brief(upper)}";
                if (lower.Type != upper.Type)
                {
                    throw ExpressionErrors.Invalid(_member, $"The BETWEEN operator requires same data type for lower and upper bounds; {bounds}");
                }

                if (DynamoKeyOrder.Compare(lower, upper) > 0)
                {
                    throw ExpressionErrors.Invalid(_member, $"The BETWEEN operator requires upper bound to be greater than or equal to lower bound; {bounds}");
                }
            }

            return new BetweenCondition(left, low, high);
        }

        if (EatKeyword("IN"))
        {
            Expect(TokenKind.Open);
            var candidates = new List<Operand> { Operand() };
            while (Eat(TokenKind.Comma))
            {
                candidates.Add(Operand());
            }

            Expect(TokenKind.Close);
            if (candidates.Count > ExpressionErrors.MaxInOperands)
            {
                throw ExpressionErrors.Invalid(_member, $"The IN operator is provided with too many operands; number of operands: {candidates.Count.ToString(CultureInfo.InvariantCulture)}");
            }

            return new InCondition(left, candidates);
        }

        var token = Next();
        if (token.Kind != TokenKind.Compare)
        {
            _at--;
            throw SyntaxError();
        }

        var right = Operand();
        CheckDistinct(token.Text, left, right);
        if (token.Operator is not (CompareOperator.Equal or CompareOperator.NotEqual))
        {
            CheckOrderable(token.Text, left);
            CheckOrderable(token.Text, right);
        }

        return new CompareCondition(token.Operator, left, right);
    }

    /// <summary>Refuses a literal an ordering operator can't order: only strings, numbers and binaries are.</summary>
    private void CheckOrderable(string op, Operand operand)
    {
        if (operand is ValueOperand { Value.Type: not (DynamoType.S or DynamoType.N or DynamoType.B) } value)
        {
            throw ExpressionErrors.IncorrectOperand(_member, op, value.Value.Type);
        }
    }

    /// <summary>Refuses a comparison or function whose first operand is the same path as another of its operands.</summary>
    private void CheckDistinct(string function, Operand first, Operand other)
    {
        if (first is PathOperand { Path: var path } && other is PathOperand { Path: var otherPath } && path.Equals(otherPath))
        {
            throw ExpressionErrors.Invalid(_member, $"The first operand must be distinct from the remaining operands for this operator or function; operator: {function}, first operand: {path.RenderTokens()}");
        }
    }

    /// <summary>
    /// A parenthesised operand of a comparison, <c>(size(a)) = :n</c>, or null (with nothing
    /// read) when the parenthesis opens a condition instead.
    /// </summary>
    private Operand? GroupedOperand()
    {
        var (at, depth, operators, reserved) = (_at, _depth, _operators, _reserved);
        try
        {
            _at++;
            var operand = Operand();
            if (Eat(TokenKind.Close)
                && (Peek() is { Kind: TokenKind.Compare }
                    || Peek() is { Kind: TokenKind.Word } word
                        && (string.Equals(word.Text, "BETWEEN", StringComparison.OrdinalIgnoreCase) || string.Equals(word.Text, "IN", StringComparison.OrdinalIgnoreCase))))
            {
                return operand;
            }
        }
        catch (DynamoDbException)
        {
            // Not an operand: read it again as a condition.
        }

        (_at, _depth, _operators, _reserved) = (at, depth, operators, reserved);
        return null;
    }

    private static readonly string[] AttributeTypeNames = ["S", "SS", "N", "NS", "B", "BS", "BOOL", "NULL", "L", "M"];

    private ExpressionCondition Function(string name)
    {
        _at += 2;
        _operators++;
        ExpressionCondition node;
        switch (name)
        {
            case "attribute_exists":
                node = new ExistsCondition(Path(), true);
                break;
            case "attribute_not_exists":
                node = new ExistsCondition(Path(), false);
                break;
            case "attribute_type":
            {
                var path = Path();
                Expect(TokenKind.Comma);
                var wanted = Operand();
                if (wanted is ValueOperand { Value: var value })
                {
                    if (value.Type != DynamoType.S)
                    {
                        throw ExpressionErrors.IncorrectOperand(_member, "attribute_type", value.Type);
                    }

                    if (Array.IndexOf(AttributeTypeNames, value.S) < 0)
                    {
                        throw ExpressionErrors.Invalid(_member, $"Invalid attribute type name found; type: {value.S}, valid types: {{{string.Join(",", AttributeTypeNames)}}}");
                    }
                }

                CheckDistinct("attribute_type", new PathOperand(path), wanted);
                node = new AttributeTypeCondition(path, wanted);
                break;
            }

            case "begins_with":
            {
                var value = Operand();
                Expect(TokenKind.Comma);
                var prefix = Operand();
                if (prefix is ValueOperand { Value: var literal } && literal.Type is not (DynamoType.S or DynamoType.B))
                {
                    throw ExpressionErrors.IncorrectOperand(_member, "begins_with", literal.Type);
                }

                CheckDistinct("begins_with", value, prefix);
                node = new BeginsWithCondition(value, prefix);
                break;
            }

            case "contains":
            {
                var haystack = Operand();
                Expect(TokenKind.Comma);
                var needle = Operand();
                CheckDistinct("contains", haystack, needle);
                node = new ContainsCondition(haystack, needle);
                break;
            }

            default:
                throw ExpressionErrors.Invalid(_member, $"Invalid function name; function: {name}");
        }

        Expect(TokenKind.Close);
        return node;
    }

    private Operand Operand()
    {
        if (Peek() is { Kind: TokenKind.Placeholder } placeholder)
        {
            _at++;
            return new ValueOperand(ResolvePlaceholder(placeholder.Text));
        }

        if (string.Equals(PeekCall(), "size", StringComparison.Ordinal))
        {
            _at += 2;
            _operators++;
            var path = Path();
            Expect(TokenKind.Close);
            return new SizeOperand(path);
        }

        if (Peek() is { Kind: TokenKind.Word or TokenKind.Alias })
        {
            return new PathOperand(Path());
        }

        throw SyntaxError();
    }

    // Updates

    private static readonly string[] UpdateSections = ["SET", "REMOVE", "ADD", "DELETE"];

    private List<UpdateAction> UpdateActions()
    {
        var actions = new List<UpdateAction>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (!AtEnd)
        {
            var section = UpdateSections.FirstOrDefault(EatKeyword) ?? throw SyntaxError();
            if (!seen.Add(section))
            {
                throw ExpressionErrors.Invalid(_member, $"The \"{section}\" section can only be used once in an update expression;");
            }

            do
            {
                var path = Path();
                switch (section)
                {
                    case "SET":
                        if (Peek() is not { Kind: TokenKind.Compare, Operator: CompareOperator.Equal })
                        {
                            throw SyntaxError();
                        }

                        _at++;
                        actions.Add(new SetAction(path, SetValue()));
                        break;
                    case "REMOVE":
                        actions.Add(new RemoveAction(path));
                        break;
                    case "ADD":
                        actions.Add(new AddAction(path, ActionValue()));
                        break;
                    default:
                        actions.Add(new DeleteAction(path, ActionValue()));
                        break;
                }
            }
            while (Eat(TokenKind.Comma));
        }

        if (actions.Count == 0)
        {
            throw SyntaxError();
        }

        return actions;
    }

    private DynamoValue ActionValue()
    {
        var token = Next();
        if (token.Kind != TokenKind.Placeholder)
        {
            _at--;
            throw SyntaxError();
        }

        return ResolvePlaceholder(token.Text);
    }

    private SetValue SetValue()
    {
        var left = SetOperand();

        // A parenthesized sum or difference is a whole value, not an operand of another:
        // SET n = (n + :a) - :b is a syntax error at the '-'.
        if (left is GroupSetOperand && Peek() is { Kind: TokenKind.Plus or TokenKind.Minus })
        {
            throw SyntaxError();
        }

        if (Eat(TokenKind.Plus))
        {
            _operators++;
            return new SetValue(left, SetOperand(), Subtract: false);
        }

        if (Eat(TokenKind.Minus))
        {
            _operators++;
            return new SetValue(left, SetOperand(), Subtract: true);
        }

        return new SetValue(left, null, Subtract: false);
    }

    private SetOperand SetOperand()
    {
        if (PeekCall() is { } name)
        {
            _at += 2;
            _operators++;
            Descend();
            SetOperand operand;
            switch (name)
            {
                case "if_not_exists":
                {
                    var path = Path();
                    Expect(TokenKind.Comma);
                    operand = new IfNotExistsOperand(path, SetOperand());
                    break;
                }

                case "list_append":
                {
                    var first = SetOperand();
                    Expect(TokenKind.Comma);
                    operand = new ListAppendOperand(first, SetOperand());
                    break;
                }

                default:
                    throw ExpressionErrors.Invalid(_member, $"Invalid function name; function: {name}");
            }

            _depth--;
            Expect(TokenKind.Close);
            return operand;
        }

        // Parentheses may group a sum or difference: SET n = (n - :d).
        if (Eat(TokenKind.Open))
        {
            Descend();
            var grouped = SetValue();
            _depth--;
            Expect(TokenKind.Close);
            return new GroupSetOperand(grouped);
        }

        if (Peek() is { Kind: TokenKind.Placeholder } placeholder)
        {
            _at++;
            return new ValueSetOperand(ResolvePlaceholder(placeholder.Text));
        }

        if (Peek() is { Kind: TokenKind.Word or TokenKind.Alias })
        {
            return new PathSetOperand(Path());
        }

        throw SyntaxError();
    }
}
