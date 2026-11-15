using LocalSqsSnsMessaging.DynamoDb.Model;

namespace LocalSqsSnsMessaging.DynamoDb.Expressions;

/// <summary>
/// DynamoDB's legacy conditional parameters (<c>Expected</c>, <c>QueryFilter</c>,
/// <c>ScanFilter</c>, <c>ConditionalOperator</c>), translated into the same conditions the
/// expressions parse to. The .NET Document Model and <c>DynamoDBContext</c> still send some of them.
/// </summary>
internal static class LegacyConditions
{
    /// <summary>A value as DynamoDB's messages abbreviate it: <c>{N:100}</c>, <c>{S:z}</c>.</summary>
    public static string Brief(DynamoValue value) => value.Type switch
    {
        DynamoType.S => $"{{S:{value.S}}}",
        DynamoType.N => $"{{N:{value.N}}}",
        DynamoType.B => $"{{B:{Convert.ToBase64String(value.B)}}}",
        DynamoType.BOOL => $"{{BOOL:{(value.Bool ? "true" : "false")}}}",
        DynamoType.NULL => "{NULL:true}",
        DynamoType.SS => $"{{SS:[{string.Join(", ", value.SS)}]}}",
        DynamoType.NS => $"{{NS:[{string.Join(", ", value.NS)}]}}",
        _ => $"{{{value.Type}:{value}}}",
    };

    public static void CheckConditionalOperator(string? conditionalOperator, string companionMember, int elements)
    {
        if (conditionalOperator is null)
        {
            return;
        }

        CheckConditionalOperatorValue(conditionalOperator, companionMember);

        var clause = elements switch
        {
            0 => "ConditionalOperator cannot be used without Filter or Expected",
            1 => "ConditionalOperator can only be used when Filter or Expected has two or more elements",
            _ => null,
        };
        if (clause is not null)
        {
            throw companionMember == "Expected" ? DynamoDbErrors.ValidationClause(clause) : DynamoDbErrors.Validation(clause);
        }
    }

    /// <summary>Refuses a ConditionalOperator other than AND or OR, as DynamoDB words it (AND as ALL).</summary>
    public static void CheckConditionalOperatorValue(string? conditionalOperator, string companionMember)
    {
        if (conditionalOperator is null or "AND" or "OR")
        {
            return;
        }

        const string clause = "Failed to satisfy constraint: Member must satisfy enum value set: [ALL, OR]";
        throw companionMember == "Expected" ? DynamoDbErrors.ValidationClause(clause) : DynamoDbErrors.Validation(clause);
    }

    private static bool Any(string? conditionalOperator) => conditionalOperator == "OR";

    private static int? RequiredOperands(string op) => op switch
    {
        "NULL" or "NOT_NULL" => 0,
        "BETWEEN" => 2,
        "IN" => int.MaxValue,
        "EQ" or "NE" or "LE" or "LT" or "GE" or "GT" or "CONTAINS" or "NOT_CONTAINS" or "BEGINS_WITH" => 1,
        _ => null,
    };

    /// <summary>Refuses an unknown comparison operator, or one given the wrong number of values.</summary>
    public static void CheckComparison(string op, IReadOnlyList<DynamoValue> operands)
    {
        var required = RequiredOperands(op)
            ?? throw DynamoDbErrors.Validation($"ComparisonOperator must be one of EQ, NE, IN, LE, LT, GE, GT, BETWEEN, NOT_NULL, NULL, CONTAINS, NOT_CONTAINS or BEGINS_WITH, got {op}");
        // The legacy IN takes any number of values, unlike the expression IN's 100.
        var countOk = required == int.MaxValue ? operands.Count > 0 : operands.Count == required;
        if (!countOk)
        {
            throw DynamoDbErrors.InvalidParameter($"Invalid number of argument(s) for the {op} ComparisonOperator");
        }

        if (op == "BEGINS_WITH" && operands[0].Type is not (DynamoType.S or DynamoType.B))
        {
            throw DynamoDbErrors.InvalidParameter($"ComparisonOperator {op} is not valid for {operands[0].Type} AttributeValue type");
        }

        if (op is "LT" or "LE" or "GT" or "GE" or "BETWEEN" && operands.Any(o => o.Type is not (DynamoType.S or DynamoType.N or DynamoType.B)))
        {
            var bad = operands.First(o => o.Type is not (DynamoType.S or DynamoType.N or DynamoType.B));
            throw DynamoDbErrors.InvalidParameter($"ComparisonOperator {op} is not valid for {bad.Type} AttributeValue type");
        }

        if (op == "BETWEEN" && operands[0].Type == operands[1].Type && DynamoKeyOrder.Compare(operands[0], operands[1]) > 0)
        {
            throw DynamoDbErrors.Validation($"The BETWEEN operator requires upper bound to be greater than or equal to lower bound; lower bound operand: AttributeValue: {Brief(operands[0])}, upper bound operand: AttributeValue: {Brief(operands[1])}");
        }
    }

    /// <summary>The condition an <c>Expected</c> map states.</summary>
    public static ExpressionCondition FromExpected(Dictionary<string, ExpectedAttributeValue> expected, string? conditionalOperator)
    {
        CheckConditionalOperator(conditionalOperator, "Expected", expected.Count);
        var any = Any(conditionalOperator);
        var conditions = new List<ExpressionCondition>();
        foreach (var (name, entry) in expected)
        {
            conditions.Add(FromExpectedEntry(name, entry));
        }

        return any ? new OrCondition(conditions) : new AndCondition(conditions);
    }

    private static LegacyComparison FromExpectedEntry(string name, ExpectedAttributeValue entry)
    {
        var hasList = entry.AttributeValueList is { Count: > 0 };
        if (entry.ComparisonOperator is not null && entry.Exists is not null)
        {
            throw DynamoDbErrors.ValidationClause($"One or more parameter values were invalid: Exists and ComparisonOperator cannot be used together for Attribute: {name}");
        }

        if (hasList && entry.Value is not null)
        {
            throw DynamoDbErrors.ValidationClause($"One or more parameter values were invalid: Value and AttributeValueList cannot be used together for Attribute: {name}");
        }

        if (entry.ComparisonOperator is { } op)
        {
            List<DynamoValue> values = hasList ? entry.AttributeValueList! : entry.Value is null ? [] : [entry.Value];
            if (values.Count == 0 && RequiredOperands(op) is > 0)
            {
                throw DynamoDbErrors.ValidationClause($"One or more parameter values were invalid: Value or AttributeValueList must be used with ComparisonOperator: {op} for Attribute: {name}");
            }

            CheckComparison(op, values);
            return new LegacyComparison(name, op, values);
        }

        return (entry.Value, entry.Exists) switch
        {
            ({ } value, null or true) => new LegacyComparison(name, "EQ", [value]),
            (not null, false) => throw DynamoDbErrors.ValidationClause($"One or more parameter values were invalid: Value cannot be used when Exists is false for Attribute: {name}"),
            (null, true) => throw DynamoDbErrors.ValidationClause($"One or more parameter values were invalid: Value must be provided when Exists is true for Attribute: {name}"),
            (null, false) => new LegacyComparison(name, "NULL", []),
            _ => throw DynamoDbErrors.ValidationClause($"One or more parameter values were invalid: Value must be provided when Exists is null for Attribute: {name}"),
        };
    }

    /// <summary>The condition a <c>QueryFilter</c> or <c>ScanFilter</c> states.</summary>
    public static ExpressionCondition FromFilter(string member, Dictionary<string, Model.Condition> filter, string? conditionalOperator)
    {
        CheckConditionalOperator(conditionalOperator, member, filter.Count);
        var any = Any(conditionalOperator);
        var conditions = new List<ExpressionCondition>();
        foreach (var (name, condition) in filter)
        {
            var operands = condition.AttributeValueList ?? [];
            var op = condition.ComparisonOperator ?? string.Empty;
            CheckComparison(op, operands);
            conditions.Add(new LegacyComparison(name, op, operands));
        }

        return any ? new OrCondition(conditions) : new AndCondition(conditions);
    }

    /// <summary>
    /// Refuses a request mixing expression parameters with the legacy ones they replace:
    /// <c>Can not use both expression and non-expression parameters in the same request</c>.
    /// </summary>
    public static void CheckNoMix(IReadOnlyList<(string Member, bool Sent)> legacy, IReadOnlyList<(string Member, bool Sent)> expressions)
    {
        var sentExpressions = expressions.Where(e => e.Sent).Select(e => e.Member).ToList();
        var sentLegacy = legacy.Where(l => l.Sent).Select(l => l.Member).ToList();
        if (sentExpressions.Count == 0 || sentLegacy.Count == 0)
        {
            return;
        }

        var clause = $"Can not use both expression and non-expression parameters in the same request: Non-expression parameters: {{{string.Join(", ", sentLegacy)}}} Expression parameters: {{{string.Join(", ", sentExpressions)}}}";
        var enveloped = sentLegacy.Concat(sentExpressions).Any(m => m is "UpdateExpression" or "ConditionExpression" or "Expected");
        throw enveloped ? DynamoDbErrors.ValidationClause(clause) : DynamoDbErrors.Validation(clause);
    }
}
