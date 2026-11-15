namespace LocalSqsSnsMessaging.DynamoDb.Expressions;

/// <summary>
/// Applies a projection (a <c>ProjectionExpression</c>'s paths, or <c>AttributesToGet</c>) to an
/// item: the result holds only the projected values, nested as they are in the item. Projected
/// list elements keep their order and close up (<c>l[0], l[2]</c> gives a two-element list).
/// </summary>
internal static class ItemProjection
{
    public static Dictionary<string, DynamoValue> Apply(IReadOnlyDictionary<string, DynamoValue> item, IReadOnlyList<DocumentPath> paths)
    {
        var root = new Node();
        foreach (var path in paths)
        {
            var value = path.Get(item);
            if (value is null)
            {
                continue;
            }

            var node = root;
            foreach (var step in path.Steps)
            {
                node = step.IsIndex ? node.Element(step.Index) : node.Child(step.Name!);
            }

            node.Value = value;
        }

        var result = new Dictionary<string, DynamoValue>(StringComparer.Ordinal);
        if (root.Children is not null)
        {
            foreach (var (name, child) in root.Children)
            {
                result[name] = child.Build();
            }
        }

        return result;
    }

    /// <summary>Only the named top-level attributes of <paramref name="item"/>.</summary>
    public static Dictionary<string, DynamoValue> Attributes(IReadOnlyDictionary<string, DynamoValue> item, IEnumerable<string> names)
    {
        var result = new Dictionary<string, DynamoValue>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (item.TryGetValue(name, out var value))
            {
                result[name] = value;
            }
        }

        return result;
    }

    private sealed class Node
    {
        public DynamoValue? Value { get; set; }

        public Dictionary<string, Node>? Children { get; private set; }

        public SortedDictionary<int, Node>? Elements { get; private set; }

        public Node Child(string name)
        {
            Children ??= new Dictionary<string, Node>(StringComparer.Ordinal);
            if (!Children.TryGetValue(name, out var child))
            {
                child = new Node();
                Children[name] = child;
            }

            return child;
        }

        public Node Element(int index)
        {
            Elements ??= [];
            if (!Elements.TryGetValue(index, out var element))
            {
                element = new Node();
                Elements[index] = element;
            }

            return element;
        }

        public DynamoValue Build()
        {
            if (Value is not null)
            {
                return Value;
            }

            if (Elements is not null)
            {
                return DynamoValue.FromList(Elements.Values.Where(e => e.HasValue).Select(e => e.Build()).ToList());
            }

            var map = new Dictionary<string, DynamoValue>(StringComparer.Ordinal);
            foreach (var (name, child) in Children ?? [])
            {
                if (child.HasValue)
                {
                    map[name] = child.Build();
                }
            }

            return DynamoValue.FromMap(map);
        }

        private bool HasValue => Value is not null || (Children?.Values.Any(c => c.HasValue) ?? false) || (Elements?.Values.Any(e => e.HasValue) ?? false);
    }
}
