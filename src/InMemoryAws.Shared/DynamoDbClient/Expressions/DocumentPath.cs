namespace InMemoryAws.DynamoDb.Expressions;

/// <summary>One step of a document path: an attribute (or map key) name, or a list index.</summary>
internal readonly record struct PathStep(string? Name, int Index)
{
    public bool IsIndex => Name is null;

    public static PathStep Attribute(string name) => new(name, -1);

    public static PathStep Element(int index) => new(null, index);
}

/// <summary>
/// A document path into an item: <c>a.b[2].c</c>. Values are immutable, so setting or removing
/// a nested value rebuilds the maps and lists along the path.
/// </summary>
internal sealed class DocumentPath : IEquatable<DocumentPath>
{
    public const string InvalidForUpdate = "The document path provided in the update expression is invalid for update";

    public DocumentPath(IReadOnlyList<PathStep> steps)
    {
        Steps = steps;
    }

    public static DocumentPath Attribute(string name) => new([PathStep.Attribute(name)]);

    public IReadOnlyList<PathStep> Steps { get; }

    /// <summary>The top-level attribute the path starts at.</summary>
    public string AttributeName => Steps[0].Name!;

    public bool IsTopLevel => Steps.Count == 1;

    /// <summary><c>a.b[1]</c>.</summary>
    public string Render() => RenderPrefix(Steps.Count);

    public string RenderPrefix(int steps)
    {
        var text = new StringBuilder();
        for (var i = 0; i < steps; i++)
        {
            var step = Steps[i];
            if (step.IsIndex)
            {
                text.Append('[').Append(step.Index.ToString(CultureInfo.InvariantCulture)).Append(']');
            }
            else
            {
                if (text.Length > 0)
                {
                    text.Append('.');
                }

                text.Append(step.Name);
            }
        }

        return text.ToString();
    }

    /// <summary><c>[a, b, [1]]</c>, as DynamoDB lists a path's tokens in an overlap refusal.</summary>
    public string RenderTokens(int? steps = null) =>
        "[" + string.Join(", ", Steps.Take(steps ?? Steps.Count).Select(s => s.IsIndex ? $"[{s.Index.ToString(CultureInfo.InvariantCulture)}]" : s.Name)) + "]";

    public DynamoValue? Get(IReadOnlyDictionary<string, DynamoValue>? item)
    {
        if (item is null || !item.TryGetValue(Steps[0].Name!, out var current))
        {
            return null;
        }

        for (var i = 1; i < Steps.Count; i++)
        {
            var step = Steps[i];
            if (step.IsIndex)
            {
                if (current.Type != DynamoType.L || step.Index >= current.L.Count)
                {
                    return null;
                }

                current = current.L[step.Index];
            }
            else
            {
                if (current.Type != DynamoType.M || !current.M.TryGetValue(step.Name!, out var next))
                {
                    return null;
                }

                current = next;
            }
        }

        return current;
    }

    /// <summary>
    /// Sets the value at the path in <paramref name="item"/> (which is changed in place at the top
    /// level). The parent must exist; an index past a list's end appends.
    /// </summary>
    public void Set(Dictionary<string, DynamoValue> item, DynamoValue value)
    {
        if (IsTopLevel)
        {
            item[AttributeName] = value;
            return;
        }

        if (!item.TryGetValue(AttributeName, out var parent))
        {
            throw DynamoDbErrors.Validation(InvalidForUpdate);
        }

        item[AttributeName] = SetWithin(parent, 1, value) ?? throw DynamoDbErrors.Validation(InvalidForUpdate);
    }

    private DynamoValue? SetWithin(DynamoValue target, int step, DynamoValue value)
    {
        var current = Steps[step];
        var last = step == Steps.Count - 1;
        if (current.IsIndex)
        {
            if (target.Type != DynamoType.L)
            {
                return null;
            }

            var list = target.L.ToList();
            if (last)
            {
                if (current.Index < list.Count)
                {
                    list[current.Index] = value;
                }
                else
                {
                    list.Add(value);
                }
            }
            else
            {
                if (current.Index >= list.Count)
                {
                    return null;
                }

                var replaced = SetWithin(list[current.Index], step + 1, value);
                if (replaced is null)
                {
                    return null;
                }

                list[current.Index] = replaced;
            }

            return DynamoValue.FromList(list);
        }

        if (target.Type != DynamoType.M)
        {
            return null;
        }

        var map = new Dictionary<string, DynamoValue>(target.M.Count + 1, StringComparer.Ordinal);
        foreach (var entry in target.M)
        {
            map[entry.Key] = entry.Value;
        }

        if (last)
        {
            map[current.Name!] = value;
        }
        else
        {
            if (!map.TryGetValue(current.Name!, out var child))
            {
                return null;
            }

            var replaced = SetWithin(child, step + 1, value);
            if (replaced is null)
            {
                return null;
            }

            map[current.Name!] = replaced;
        }

        return DynamoValue.FromMap(map);
    }

    /// <summary>Removes the value at the path, returning whether there was one.</summary>
    public bool Remove(Dictionary<string, DynamoValue> item)
    {
        if (IsTopLevel)
        {
            return item.Remove(AttributeName);
        }

        if (!item.TryGetValue(AttributeName, out var parent))
        {
            return false;
        }

        var replaced = RemoveWithin(parent, 1);
        if (replaced is null)
        {
            return false;
        }

        item[AttributeName] = replaced;
        return true;
    }

    private DynamoValue? RemoveWithin(DynamoValue target, int step)
    {
        var current = Steps[step];
        var last = step == Steps.Count - 1;
        if (current.IsIndex)
        {
            if (target.Type != DynamoType.L || current.Index >= target.L.Count)
            {
                return null;
            }

            var list = target.L.ToList();
            if (last)
            {
                list.RemoveAt(current.Index);
            }
            else
            {
                var replaced = RemoveWithin(list[current.Index], step + 1);
                if (replaced is null)
                {
                    return null;
                }

                list[current.Index] = replaced;
            }

            return DynamoValue.FromList(list);
        }

        if (target.Type != DynamoType.M || !target.M.TryGetValue(current.Name!, out var child))
        {
            return null;
        }

        var map = new Dictionary<string, DynamoValue>(target.M.Count, StringComparer.Ordinal);
        foreach (var entry in target.M)
        {
            map[entry.Key] = entry.Value;
        }

        if (last)
        {
            map.Remove(current.Name!);
        }
        else
        {
            var replaced = RemoveWithin(child, step + 1);
            if (replaced is null)
            {
                return null;
            }

            map[current.Name!] = replaced;
        }

        return DynamoValue.FromMap(map);
    }

    /// <summary>
    /// Whether the path steps into a value that is there but can't hold the next step: a name
    /// within something other than a map, or an index within something other than a list.
    /// </summary>
    public bool CrossesNonContainer(IReadOnlyDictionary<string, DynamoValue> item)
    {
        if (!item.TryGetValue(AttributeName, out var current))
        {
            return false;
        }

        for (var i = 1; i < Steps.Count; i++)
        {
            var step = Steps[i];
            if (step.IsIndex)
            {
                if (current.Type != DynamoType.L)
                {
                    return true;
                }

                if (step.Index >= current.L.Count)
                {
                    return false;
                }

                current = current.L[step.Index];
            }
            else
            {
                if (current.Type != DynamoType.M)
                {
                    return true;
                }

                if (!current.M.TryGetValue(step.Name!, out current!))
                {
                    return false;
                }
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="other"/> starts with every step of this path.</summary>
    public bool IsPrefixOf(DocumentPath other)
    {
        if (Steps.Count > other.Steps.Count)
        {
            return false;
        }

        for (var i = 0; i < Steps.Count; i++)
        {
            if (Steps[i] != other.Steps[i])
            {
                return false;
            }
        }

        return true;
    }

    public bool Equals(DocumentPath? other) => other is not null && other.Steps.Count == Steps.Count && IsPrefixOf(other);

    public override bool Equals(object? obj) => obj is DocumentPath other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Render());

    public override string ToString() => Render();

    /// <summary>
    /// Orders paths so that removing them one after another removes what each named: deeper list
    /// indexes first, and higher indexes before lower ones.
    /// </summary>
    public static int CompareForRemoval(DocumentPath a, DocumentPath b)
    {
        var length = Math.Min(a.Steps.Count, b.Steps.Count);
        for (var i = 0; i < length; i++)
        {
            var x = a.Steps[i];
            var y = b.Steps[i];
            if (x == y)
            {
                continue;
            }

            if (x.IsIndex && y.IsIndex)
            {
                return y.Index.CompareTo(x.Index);
            }

            return string.CompareOrdinal(y.Name ?? string.Empty, x.Name ?? string.Empty);
        }

        return b.Steps.Count.CompareTo(a.Steps.Count);
    }
}
