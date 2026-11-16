namespace LocalSqsSnsMessaging.DynamoDb.Expressions;

/// <summary>A value a <c>SET</c> action computes: an operand, or the sum or difference of two.</summary>
internal sealed record SetValue(SetOperand Left, SetOperand? Right, bool Subtract);

internal abstract class SetOperand;

internal sealed class PathSetOperand(DocumentPath path) : SetOperand
{
    public DocumentPath Path { get; } = path;
}

internal sealed class ValueSetOperand(DynamoValue value) : SetOperand
{
    public DynamoValue Value { get; } = value;
}

internal sealed class IfNotExistsOperand(DocumentPath path, SetOperand fallback) : SetOperand
{
    public DocumentPath Path { get; } = path;

    public SetOperand Fallback { get; } = fallback;
}

internal sealed class ListAppendOperand(SetOperand first, SetOperand second) : SetOperand
{
    public SetOperand First { get; } = first;

    public SetOperand Second { get; } = second;
}

/// <summary>A parenthesised value, <c>(a + :b)</c>, used as an operand.</summary>
internal sealed class GroupSetOperand(SetValue value) : SetOperand
{
    public SetValue Value { get; } = value;
}

internal abstract class UpdateAction(DocumentPath path)
{
    public DocumentPath Path { get; } = path;
}

internal sealed class SetAction(DocumentPath path, SetValue value) : UpdateAction(path)
{
    public SetValue Value { get; } = value;
}

internal sealed class RemoveAction(DocumentPath path) : UpdateAction(path);

internal sealed class AddAction(DocumentPath path, DynamoValue value) : UpdateAction(path)
{
    public DynamoValue Value { get; } = value;
}

internal sealed class DeleteAction(DocumentPath path, DynamoValue value) : UpdateAction(path)
{
    public DynamoValue Value { get; } = value;
}

/// <summary>
/// An update's actions, applied as DynamoDB applies them: every operand is read from the item as
/// it was before the update, the <c>SET</c>, <c>ADD</c> and <c>DELETE</c> actions apply in order,
/// then the <c>REMOVE</c>s (highest list index first, so each removes what it named).
/// </summary>
internal sealed class UpdateProgram(string member, IReadOnlyList<UpdateAction> actions)
{
    public const string IncorrectOperandType = "An operand in the update expression has an incorrect data type";
    public const string MissingOperandAttribute = "The provided expression refers to an attribute that does not exist in the item";

    public string Member { get; } = member;

    public IReadOnlyList<UpdateAction> Actions { get; } = actions;

    /// <summary>Applies the actions to <paramref name="item"/> in place, returning the paths that changed.</summary>
    public List<DocumentPath> Apply(Dictionary<string, DynamoValue> item)
    {
        var before = new Dictionary<string, DynamoValue>(item, StringComparer.Ordinal);
        var changed = new List<DocumentPath>();
        var removals = new List<DocumentPath>();
        foreach (var action in InApplyOrder(Actions))
        {
            switch (action)
            {
                case SetAction set:
                {
                    var value = DynamoValueRules.Canonicalize(Evaluate(set.Value, before), enveloped: false);
                    CheckValue(value);
                    set.Path.Set(item, value);
                    changed.Add(set.Path);
                    break;
                }

                case RemoveAction remove:
                    if (remove.Path.CrossesNonContainer(before))
                    {
                        throw DynamoDbErrors.Validation(DocumentPath.InvalidForUpdate);
                    }

                    removals.Add(remove.Path);
                    break;
                case AddAction add:
                {
                    var added = DynamoValueRules.Canonicalize(Add(add, before), enveloped: false);
                    CheckValue(added);
                    add.Path.Set(item, added);
                    changed.Add(add.Path);
                    break;
                }

                case DeleteAction delete:
                    if (Delete(delete, before, item))
                    {
                        changed.Add(delete.Path);
                    }

                    break;
            }
        }

        removals.Sort(DocumentPath.CompareForRemoval);
        foreach (var path in removals)
        {
            if (path.Remove(item))
            {
                changed.Add(path);
            }
        }

        return changed;
    }

    /// <summary>
    /// The actions in the order they apply: as written, except that <c>SET</c>s of elements of the
    /// same list apply in ascending index order, so several past the list's end append in index
    /// order, whatever order the clauses name them in.
    /// </summary>
    private static UpdateAction[] InApplyOrder(IReadOnlyList<UpdateAction> actions)
    {
        static string? ListOf(UpdateAction action) =>
            action is SetAction { Path: { Steps: { Count: > 1 } steps } path } && steps[^1].IsIndex
                ? path.RenderPrefix(steps.Count - 1)
                : null;

        var ordered = actions.ToArray();
        foreach (var group in Enumerable.Range(0, ordered.Length)
                     .Select(position => (Position: position, List: ListOf(ordered[position])))
                     .Where(entry => entry.List is not null)
                     .GroupBy(entry => entry.List, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1)
                     .ToList())
        {
            var positions = group.Select(entry => entry.Position).ToList();
            var byIndex = positions.Select(position => ordered[position]).OrderBy(action => action.Path.Steps[^1].Index).ToList();
            for (var i = 0; i < positions.Count; i++)
            {
                ordered[positions[i]] = byIndex[i];
            }
        }

        return ordered;
    }

    /// <summary>
    /// What DynamoDB charges the update's own request shape against the 400 KB item limit,
    /// apart from the item it finishes: a few bytes for the request, a fixed cost per action
    /// (19 for <c>SET</c> and <c>ADD</c>, 1 for <c>REMOVE</c> and <c>DELETE</c>), the paths the
    /// action names, the values <c>SET</c> and <c>ADD</c> write, and a fixed cost per arithmetic
    /// or function. A <c>DELETE</c>'s set, and every value a condition reads, cost nothing here.
    /// </summary>
    public long RequestShapeBytes()
    {
        var bytes = 3L;
        foreach (var action in Actions)
        {
            bytes += action switch
            {
                SetAction set => 19 + PathBytes(set.Path) + ValueBytes(set.Value),
                AddAction add => 19 + PathBytes(add.Path) + add.Value.Size(),
                _ => 1 + PathBytes(action.Path),
            };
        }

        return bytes;
    }

    private static long PathBytes(DocumentPath path)
    {
        var bytes = 0L;
        for (var i = 0; i < path.Steps.Count; i++)
        {
            var step = path.Steps[i];
            bytes += (i == 0 ? 0 : 4) + (step.IsIndex ? 0 : Encoding.UTF8.GetByteCount(step.Name!));
        }

        return bytes;
    }

    private static long ValueBytes(SetValue value) =>
        value.Right is null ? OperandBytes(value.Left) : 49 + OperandBytes(value.Left) + OperandBytes(value.Right);

    private static long OperandBytes(SetOperand operand) => operand switch
    {
        ValueSetOperand value => value.Value.Size(),
        PathSetOperand path => PathBytes(path.Path),
        IfNotExistsOperand ifNotExists => 54 + PathBytes(ifNotExists.Path) + OperandBytes(ifNotExists.Fallback),
        ListAppendOperand append => 49 + OperandBytes(append.First) + OperandBytes(append.Second),
        GroupSetOperand group => ValueBytes(group.Value),
        _ => 0,
    };

    private static void CheckValue(DynamoValue value)
    {
        if (value.Depth() > DynamoValueRules.MaxDepth)
        {
            throw DynamoDbErrors.ValidationClause(DynamoValueRules.NestingExceeded);
        }

        if (value.Size() > DynamoValueRules.MaxItemBytes)
        {
            throw DynamoDbErrors.Validation(DynamoValueRules.ItemSizeExceeded);
        }
    }

    private DynamoValue Evaluate(SetValue value, Dictionary<string, DynamoValue> before)
    {
        var left = Evaluate(value.Left, before);
        if (value.Right is null)
        {
            return left;
        }

        var right = Evaluate(value.Right, before);
        if (left.Type != DynamoType.N || right.Type != DynamoType.N)
        {
            var (offending, operand) = left.Type == DynamoType.N ? (right, value.Right) : (left, value.Left);
            throw IncorrectOperand(value.Subtract ? "-" : "+", offending.Type, operand);
        }

        return DynamoValue.FromNumber(Arithmetic(left, right, value.Subtract));
    }

    /// <summary>The sum or difference of two numbers, rounded to 38 significant digits as DynamoDB rounds it.</summary>
    public static DynamoNumber Arithmetic(DynamoValue left, DynamoValue right, bool subtract)
    {
        var a = DynamoValueRules.ParseNumber(left.N, enveloped: false);
        var b = DynamoValueRules.ParseNumber(right.N, enveloped: false);
        var fault = DynamoNumber.Add(a, subtract ? b.Negate() : b, out var result);
        return fault == DynamoNumberFault.None ? result : throw DynamoValueRules.NumberRefusal(string.Empty, fault, enveloped: false);
    }

    private DynamoValue Evaluate(SetOperand operand, Dictionary<string, DynamoValue> before)
    {
        switch (operand)
        {
            case ValueSetOperand value:
                return value.Value;
            case PathSetOperand path:
                return path.Path.Get(before) ?? throw DynamoDbErrors.Validation(MissingOperandAttribute);
            case IfNotExistsOperand ifNotExists:
                return ifNotExists.Path.Get(before) ?? Evaluate(ifNotExists.Fallback, before);
            case GroupSetOperand group:
                return Evaluate(group.Value, before);
            case ListAppendOperand append:
            {
                var first = Evaluate(append.First, before);
                var second = Evaluate(append.Second, before);
                if (first.Type != DynamoType.L || second.Type != DynamoType.L)
                {
                    var (offending, offendingOperand) = first.Type == DynamoType.L ? (second, append.Second) : (first, append.First);
                    throw IncorrectOperand("list_append", offending.Type, offendingOperand);
                }

                var appended = DynamoValue.FromList(first.L.Concat(second.L).ToList());
                if (appended.Size() > DynamoValueRules.MaxItemBytes)
                {
                    throw DynamoDbErrors.Validation(DynamoValueRules.ItemSizeExceeded);
                }

                return appended;
            }

            default:
                throw new InvalidOperationException("Unknown operand.");
        }
    }

    private static string TypeSetName(DynamoType type) => type switch
    {
        DynamoType.S => "STRING",
        DynamoType.N => "NUMBER",
        DynamoType.B => "BINARY",
        DynamoType.SS => "STRING_SET",
        DynamoType.NS => "NUMBER_SET",
        DynamoType.BS => "BINARY_SET",
        DynamoType.M => "MAP",
        DynamoType.L => "LIST",
        DynamoType.NULL => "NULL",
        _ => "BOOL",
    };

    /// <summary>
    /// An operand of the wrong type. A value the expression itself supplies is refused as an
    /// invalid UpdateExpression (inside the envelope), or bare for a PartiQL statement; a value
    /// read from the stored item is refused as an operand of the wrong type, as DynamoDB refuses it
    /// only once it has read the item.
    /// </summary>
    private DynamoDbException IncorrectOperand(string operatorOrFunction, DynamoType type, SetOperand operand) =>
        Member != "UpdateExpression"
            ? DynamoDbErrors.Validation($"Incorrect operand type for operator or function; operator or function: {operatorOrFunction}, operand type: {type}")
            : ReadsItem(operand)
                ? DynamoDbErrors.Validation(IncorrectOperandType)
                : ExpressionErrors.IncorrectOperand(Member, operatorOrFunction, type);

    /// <summary>Whether an operand's value came from the stored item rather than from the expression.</summary>
    private static bool ReadsItem(SetOperand operand) => operand switch
    {
        PathSetOperand => true,
        IfNotExistsOperand => true,
        ListAppendOperand append => ReadsItem(append.First) || ReadsItem(append.Second),
        GroupSetOperand group => ReadsItem(group.Value.Left) || (group.Value.Right is { } right && ReadsItem(right)),
        _ => false,
    };

    private DynamoDbException OperandTypeFor(string op, string typeSet, DynamoValue value) =>
        Member == "UpdateExpression"
            ? ExpressionErrors.Invalid(Member, $"Incorrect operand type for operator or function; operator: {op}, operand type: {TypeSetName(value.Type)}, typeSet: {typeSet}")
            : DynamoDbErrors.Validation(IncorrectOperandType);

    private DynamoValue Add(AddAction action, Dictionary<string, DynamoValue> before)
    {
        var value = action.Value;
        if (value.IsSet && value.SetCount == 0)
        {
            throw DynamoDbErrors.Validation($"One or more parameter values were invalid: An ADD action may not specify an empty set: {action.Path.Render()}");
        }

        // The legacy AttributeUpdates ADD also appends to a list.
        var appends = value.Type == DynamoType.L && Member == "AttributeUpdates";
        if (value.Type is not (DynamoType.N or DynamoType.NS or DynamoType.SS or DynamoType.BS) && !appends)
        {
            throw OperandTypeFor("ADD", "ALLOWED_FOR_ADD_OPERAND", value);
        }

        var held = action.Path.Get(before);
        if (held is null)
        {
            return value;
        }

        if (appends)
        {
            return held.Type == DynamoType.L
                ? DynamoValue.FromList(held.L.Concat(value.L).ToList())
                : throw DynamoDbErrors.Validation(IncorrectOperandType);
        }

        if (held.Type == DynamoType.N && value.Type == DynamoType.N)
        {
            return DynamoValue.FromNumber(Arithmetic(held, value, subtract: false));
        }

        if (held.Type != value.Type)
        {
            throw DynamoDbErrors.Validation(IncorrectOperandType);
        }

        return held.Type switch
        {
            DynamoType.SS => DynamoValue.FromStringSet(Union(held.SS, value.SS, s => s)),
            DynamoType.NS => DynamoValue.FromNumberSet(Union(held.NS, value.NS, CanonicalNumber)),
            DynamoType.BS => DynamoValue.FromBinarySet(Union(held.BS, value.BS, Convert.ToBase64String)),
            _ => throw DynamoDbErrors.Validation(IncorrectOperandType),
        };
    }

    private bool Delete(DeleteAction action, Dictionary<string, DynamoValue> before, Dictionary<string, DynamoValue> item)
    {
        var value = action.Value;
        if (value.IsSet && value.SetCount == 0)
        {
            throw DynamoDbErrors.Validation($"One or more parameter values were invalid: A DELETE action may not specify an empty set: {action.Path.Render()}");
        }

        // DynamoDB names ADD's type set in DELETE's refusal too.
        if (!value.IsSet)
        {
            throw OperandTypeFor("DELETE", "ALLOWED_FOR_ADD_OPERAND", value);
        }

        if (action.Path.CrossesNonContainer(before))
        {
            throw DynamoDbErrors.Validation(DocumentPath.InvalidForUpdate);
        }

        var held = action.Path.Get(before);
        if (held is null)
        {
            return false;
        }

        if (held.Type != value.Type)
        {
            throw DynamoDbErrors.Validation(IncorrectOperandType);
        }

        DynamoValue? remaining = held.Type switch
        {
            DynamoType.SS => Difference(held.SS, value.SS, s => s) is { Count: > 0 } ss ? DynamoValue.FromStringSet(ss) : null,
            DynamoType.NS => Difference(held.NS, value.NS, CanonicalNumber) is { Count: > 0 } ns ? DynamoValue.FromNumberSet(ns) : null,
            _ => Difference(held.BS, value.BS, Convert.ToBase64String) is { Count: > 0 } bs ? DynamoValue.FromBinarySet(bs) : null,
        };

        if (remaining is null)
        {
            action.Path.Remove(item);
        }
        else
        {
            action.Path.Set(item, remaining);
        }

        return true;
    }

    private static string CanonicalNumber(string text) => DynamoNumber.TryParse(text, out var number) ? number.ToString() : text;

    private static List<T> Union<T>(IReadOnlyList<T> held, IReadOnlyList<T> added, Func<T, string> key)
    {
        var seen = new HashSet<string>(held.Select(key), StringComparer.Ordinal);
        var members = held.ToList();
        foreach (var member in added)
        {
            if (seen.Add(key(member)))
            {
                members.Add(member);
            }
        }

        return members;
    }

    private static List<T> Difference<T>(IReadOnlyList<T> held, IReadOnlyList<T> removed, Func<T, string> key)
    {
        var gone = new HashSet<string>(removed.Select(key), StringComparer.Ordinal);
        return held.Where(member => !gone.Contains(key(member))).ToList();
    }

    /// <summary>The top-level attributes the update touched (for <c>UPDATED_OLD</c>/<c>UPDATED_NEW</c>).</summary>
    public static HashSet<string> Attributes(IEnumerable<DocumentPath> changed) =>
        new(changed.Select(p => p.AttributeName), StringComparer.Ordinal);

    /// <summary>
    /// Builds an update from legacy <c>AttributeUpdates</c>: <c>PUT</c> sets an attribute,
    /// <c>DELETE</c> removes it (or members of a set), <c>ADD</c> adds to a number or set.
    /// </summary>
    public static UpdateProgram FromAttributeUpdates(Dictionary<string, Model.AttributeValueUpdate> updates)
    {
        var actions = new List<UpdateAction>();
        foreach (var (name, update) in updates)
        {
            var path = DocumentPath.Attribute(name);
            switch (update.Action ?? "PUT")
            {
                case "PUT":
                    actions.Add(new SetAction(path, new SetValue(new ValueSetOperand(Required(name, update)), null, false)));
                    break;
                case "DELETE":
                    actions.Add(update.Value is null ? new RemoveAction(path) : new DeleteAction(path, update.Value));
                    break;
                case "ADD":
                    actions.Add(new AddAction(path, Required(name, update)));
                    break;
                default:
                    throw DynamoDbErrors.Validation($"One or more parameter values were invalid: Unknown AttributeUpdates action: {update.Action} for attribute {name}");
            }
        }

        return new UpdateProgram("AttributeUpdates", actions);
    }

    private static DynamoValue Required(string name, Model.AttributeValueUpdate update) =>
        update.Value ?? throw DynamoDbErrors.Validation($"One or more parameter values were invalid: Only DELETE action is allowed when no attribute value is specified: {name}");
}
