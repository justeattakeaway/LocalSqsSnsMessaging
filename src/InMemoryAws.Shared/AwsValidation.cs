namespace InMemoryAws;

/// <summary>
/// The clauses AWS's request validation states when a request breaks its model's constraints,
/// and the envelope it states them in:
/// <c>1 validation error detected: Value 'x' at 'tableName' failed to satisfy constraint: ...</c>.
/// </summary>
internal static class AwsValidation
{
    /// <summary>A required member that is absent.</summary>
    public static string Missing(string path) =>
        $"Value null at '{path}' failed to satisfy constraint: Member must not be null";

    /// <summary>A present value that breaks <paramref name="requirement"/>.</summary>
    public static string Clause(string value, string path, string requirement) =>
        $"Value '{value}' at '{path}' failed to satisfy constraint: {requirement}";

    /// <summary>
    /// The envelope: <c>1 validation error detected: ...</c>, or
    /// <c>N validation errors detected: ...; ...</c>.
    /// </summary>
    public static string Envelope(IReadOnlyList<string> clauses) =>
        clauses.Count == 1
            ? $"1 validation error detected: {clauses[0]}"
            : $"{clauses.Count.ToString(CultureInfo.InvariantCulture)} validation errors detected: {string.Join("; ", clauses)}";

    /// <summary>One clause in its envelope.</summary>
    public static string Envelope(string clause) => Envelope([clause]);

    public static void ThrowIfAny(List<string> clauses)
    {
        if (clauses.Count > 0)
        {
            throw new InternalValidationException(Envelope(clauses));
        }
    }
}

/// <summary>Values as AWS's Java services render them (<c>toString</c>) in validation messages.</summary>
internal static class JavaRendering
{
    public static string Boolean(bool value) => value ? "true" : "false";

    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static string Timestamp(DateTime value) => value.ToUniversalTime().ToString("ddd MMM dd HH:mm:ss 'UTC' yyyy", CultureInfo.InvariantCulture);

    /// <summary>A byte buffer, as <c>java.nio.HeapByteBuffer</c> renders one.</summary>
    public static string Blob(byte[] value) =>
        $"java.nio.HeapByteBuffer[pos=0 lim={value.Length.ToString(CultureInfo.InvariantCulture)} cap={value.Length.ToString(CultureInfo.InvariantCulture)}]";

    public static string List<T>(IEnumerable<T> values, Func<T, string> render) =>
        "[" + string.Join(", ", values.Select(render)) + "]";

    public static string Map<T>(IEnumerable<KeyValuePair<string, T>> entries, Func<T, string> render) =>
        "{" + string.Join(", ", entries.Select(e => e.Key + "=" + render(e.Value))) + "}";
}
