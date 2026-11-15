namespace LocalSqsSnsMessaging.DynamoDb.Expressions;

/// <summary>An operand of a condition: a document path, a <c>:value</c>, or <c>size(path)</c>.</summary>
internal abstract class Operand
{
    /// <summary>The operand's value in <paramref name="item"/>, or null when the path names nothing there.</summary>
    public abstract DynamoValue? Resolve(IReadOnlyDictionary<string, DynamoValue>? item);
}

internal sealed class PathOperand(DocumentPath path) : Operand
{
    public DocumentPath Path { get; } = path;

    public override DynamoValue? Resolve(IReadOnlyDictionary<string, DynamoValue>? item) => Path.Get(item);
}

internal sealed class ValueOperand(DynamoValue value) : Operand
{
    public DynamoValue Value { get; } = value;

    public override DynamoValue? Resolve(IReadOnlyDictionary<string, DynamoValue>? item) => Value;
}

internal sealed class SizeOperand(DocumentPath path) : Operand
{
    public DocumentPath Path { get; } = path;

    public override DynamoValue? Resolve(IReadOnlyDictionary<string, DynamoValue>? item)
    {
        var value = Path.Get(item);
        long? size = value?.Type switch
        {
            // DynamoDB measures a string in UTF-16 code units, not bytes.
            DynamoType.S => value.S.Length,
            DynamoType.B => value.B.Length,
            DynamoType.SS or DynamoType.NS or DynamoType.BS => value.SetCount,
            DynamoType.L => value.L.Count,
            DynamoType.M => value.M.Count,
            _ => null,
        };

        return size is null ? null : DynamoValue.FromNumber(size.Value);
    }
}

/// <summary>A condition, filter or key condition, evaluated against an item (or no item).</summary>
internal abstract class ExpressionCondition
{
    public abstract bool Evaluate(IReadOnlyDictionary<string, DynamoValue>? item);

    /// <summary>Every operand of the condition, for the checks a caller makes of them (key conditions, PartiQL).</summary>
    public virtual IEnumerable<ExpressionCondition> Children => [];
}

internal sealed class AndCondition(IReadOnlyList<ExpressionCondition> conditions) : ExpressionCondition
{
    public IReadOnlyList<ExpressionCondition> Conditions { get; } = conditions;

    public override IEnumerable<ExpressionCondition> Children => Conditions;

    public override bool Evaluate(IReadOnlyDictionary<string, DynamoValue>? item) => Conditions.All(c => c.Evaluate(item));
}

internal sealed class OrCondition(IReadOnlyList<ExpressionCondition> conditions) : ExpressionCondition
{
    public IReadOnlyList<ExpressionCondition> Conditions { get; } = conditions;

    public override IEnumerable<ExpressionCondition> Children => Conditions;

    public override bool Evaluate(IReadOnlyDictionary<string, DynamoValue>? item) => Conditions.Any(c => c.Evaluate(item));
}

internal sealed class NotCondition(ExpressionCondition inner) : ExpressionCondition
{
    public ExpressionCondition Inner { get; } = inner;

    public override IEnumerable<ExpressionCondition> Children => [Inner];

    public override bool Evaluate(IReadOnlyDictionary<string, DynamoValue>? item) => !Inner.Evaluate(item);
}

/// <summary>A condition that always holds, for a request with no condition.</summary>
internal sealed class TrueCondition : ExpressionCondition
{
    public static TrueCondition Instance { get; } = new();

    public override bool Evaluate(IReadOnlyDictionary<string, DynamoValue>? item) => true;
}

internal sealed class CompareCondition(CompareOperator op, Operand left, Operand right) : ExpressionCondition
{
    public CompareOperator Operator { get; } = op;

    public Operand Left { get; } = left;

    public Operand Right { get; } = right;

    public override bool Evaluate(IReadOnlyDictionary<string, DynamoValue>? item)
    {
        var left = Left.Resolve(item);
        var right = Right.Resolve(item);
        if (left is null || right is null)
        {
            return Operator == CompareOperator.NotEqual && (left is not null || right is not null);
        }

        return ConditionValues.Compare(Operator, left, right);
    }
}

internal sealed class BetweenCondition(Operand value, Operand low, Operand high) : ExpressionCondition
{
    public Operand Value { get; } = value;

    public Operand Low { get; } = low;

    public Operand High { get; } = high;

    public override bool Evaluate(IReadOnlyDictionary<string, DynamoValue>? item)
    {
        var value = Value.Resolve(item);
        var low = Low.Resolve(item);
        var high = High.Resolve(item);
        return value is not null && low is not null && high is not null
            && ConditionValues.Order(value, low) >= 0
            && ConditionValues.Order(value, high) <= 0;
    }
}

internal sealed class InCondition(Operand value, IReadOnlyList<Operand> candidates) : ExpressionCondition
{
    public Operand Value { get; } = value;

    public IReadOnlyList<Operand> Candidates { get; } = candidates;

    public override bool Evaluate(IReadOnlyDictionary<string, DynamoValue>? item)
    {
        var value = Value.Resolve(item);
        return value is not null && Candidates.Any(c => c.Resolve(item) is { } candidate && value.Equals(candidate));
    }
}

internal sealed class ExistsCondition(DocumentPath path, bool exists) : ExpressionCondition
{
    public DocumentPath Path { get; } = path;

    public bool Exists { get; } = exists;

    public override bool Evaluate(IReadOnlyDictionary<string, DynamoValue>? item) => (Path.Get(item) is not null) == Exists;
}

internal sealed class AttributeTypeCondition(DocumentPath path, Operand type) : ExpressionCondition
{
    public DocumentPath Path { get; } = path;

    public Operand Type { get; } = type;

    public override bool Evaluate(IReadOnlyDictionary<string, DynamoValue>? item)
    {
        var wanted = Type.Resolve(item);
        if (wanted is null || wanted.Type != DynamoType.S)
        {
            throw DynamoDbErrors.Validation("Invalid ConditionExpression: the second operand of attribute_type must be a string naming an attribute type");
        }

        return Path.Get(item) is { } value && string.Equals(value.Type.ToString(), wanted.S, StringComparison.Ordinal);
    }
}

internal sealed class BeginsWithCondition(Operand value, Operand prefix) : ExpressionCondition
{
    public Operand Value { get; } = value;

    public Operand Prefix { get; } = prefix;

    public override bool Evaluate(IReadOnlyDictionary<string, DynamoValue>? item) =>
        Value.Resolve(item) is { } value && Prefix.Resolve(item) is { } prefix && ConditionValues.BeginsWith(value, prefix);
}

internal sealed class ContainsCondition(Operand haystack, Operand needle) : ExpressionCondition
{
    public Operand Haystack { get; } = haystack;

    public Operand Needle { get; } = needle;

    public override bool Evaluate(IReadOnlyDictionary<string, DynamoValue>? item) =>
        Haystack.Resolve(item) is { } haystack && Needle.Resolve(item) is { } needle && ConditionValues.Contains(haystack, needle);
}

/// <summary>
/// A legacy <c>Expected</c>/<c>QueryFilter</c>/<c>ScanFilter</c> comparison on one attribute
/// (<c>EQ</c>, <c>NOT_CONTAINS</c>, <c>NULL</c>, ...), whose rule for an absent attribute differs
/// from the expression operators'.
/// </summary>
internal sealed class LegacyComparison(string attribute, string op, IReadOnlyList<DynamoValue> operands) : ExpressionCondition
{
    public string Attribute { get; } = attribute;

    public string Operator { get; } = op;

    public IReadOnlyList<DynamoValue> Operands { get; } = operands;

    public override bool Evaluate(IReadOnlyDictionary<string, DynamoValue>? item)
    {
        DynamoValue? held = null;
        item?.TryGetValue(Attribute, out held);
        switch (Operator)
        {
            case "NOT_NULL":
                return held is not null;
            case "NULL":
                return held is null;
        }

        if (held is null)
        {
            return Operator == "NE";
        }

        return Operator switch
        {
            "EQ" => held.Equals(Operands[0]),
            "NE" => !held.Equals(Operands[0]),
            "LT" => ConditionValues.Compare(CompareOperator.Less, held, Operands[0]),
            "LE" => ConditionValues.Compare(CompareOperator.LessOrEqual, held, Operands[0]),
            "GT" => ConditionValues.Compare(CompareOperator.Greater, held, Operands[0]),
            "GE" => ConditionValues.Compare(CompareOperator.GreaterOrEqual, held, Operands[0]),
            "CONTAINS" => ConditionValues.Contains(held, Operands[0]),
            "NOT_CONTAINS" => !ConditionValues.Contains(held, Operands[0]),
            "BEGINS_WITH" => ConditionValues.BeginsWith(held, Operands[0]),
            "IN" => Operands.Any(held.Equals),
            _ => ConditionValues.Order(held, Operands[0]) >= 0 && ConditionValues.Order(held, Operands[1]) <= 0,
        };
    }
}

/// <summary>How conditions compare values: only scalars of one type are ordered.</summary>
internal static class ConditionValues
{
    /// <summary>The order of two scalars of one type (S, N or B), or null when they aren't comparable.</summary>
    public static int? Order(DynamoValue left, DynamoValue right) =>
        left.Type == right.Type && left.Type is DynamoType.S or DynamoType.N or DynamoType.B
            ? DynamoKeyOrder.Compare(left, right)
            : null;

    public static bool Compare(CompareOperator op, DynamoValue left, DynamoValue right) => op switch
    {
        CompareOperator.Equal => left.Equals(right),
        CompareOperator.NotEqual => !left.Equals(right),
        CompareOperator.Less => Order(left, right) < 0,
        CompareOperator.LessOrEqual => Order(left, right) <= 0,
        CompareOperator.Greater => Order(left, right) > 0,
        _ => Order(left, right) >= 0,
    };

    public static bool BeginsWith(DynamoValue value, DynamoValue prefix) => (value.Type, prefix.Type) switch
    {
        (DynamoType.S, DynamoType.S) => value.S.StartsWith(prefix.S, StringComparison.Ordinal),
        (DynamoType.B, DynamoType.B) => value.B.AsSpan().StartsWith(prefix.B),
        _ => false,
    };

    public static bool Contains(DynamoValue haystack, DynamoValue needle) => (haystack.Type, needle.Type) switch
    {
        (DynamoType.S, DynamoType.S) => haystack.S.Contains(needle.S, StringComparison.Ordinal),
        (DynamoType.B, DynamoType.B) => haystack.B.AsSpan().IndexOf(needle.B) >= 0,
        (DynamoType.SS, DynamoType.S) => haystack.SS.Contains(needle.S, StringComparer.Ordinal),
        (DynamoType.NS, DynamoType.N) => haystack.NS.Any(n => DynamoValue.FromNumberText(n).Equals(needle)),
        (DynamoType.BS, DynamoType.B) => haystack.BS.Any(b => b.AsSpan().SequenceEqual(needle.B)),
        (DynamoType.L, _) => haystack.L.Any(needle.Equals),
        _ => false,
    };
}
