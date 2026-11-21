namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>
/// What the CORS ports of actual (not preflight) requests share: the <c>Access-Control-*</c> headers of
/// an answer, the tokens of its <c>Vary</c>, and the checks' polls until a new CORS rule is in effect.
/// </summary>
public static partial class S3Fidelity
{
    /// <summary>The <c>Vary</c> tokens S3 answers whenever a CORS rule grants a request.</summary>
    public static readonly IReadOnlySet<string> CorsVary =
        new HashSet<string>(StringComparer.Ordinal) { "origin", "access-control-request-headers", "access-control-request-method" };

    /// <summary><c>_cors(headers)</c>: the <c>Access-Control-*</c> headers of an answer (lower-case names), sorted by name.</summary>
    public static SortedDictionary<string, string> Cors(IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var cors = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in headers)
        {
            if (name.StartsWith("access-control-", StringComparison.Ordinal))
            {
                cors[name] = value;
            }
        }

        return cors;
    }

    /// <summary>Python's <c>==</c> of two header dictionaries.</summary>
    public static bool SameHeaders(IReadOnlyDictionary<string, string> got, IReadOnlyDictionary<string, string> want)
    {
        ArgumentNullException.ThrowIfNull(got);
        ArgumentNullException.ThrowIfNull(want);
        return got.Count == want.Count && got.All(h => want.TryGetValue(h.Key, out var value) && value == h.Value);
    }

    /// <summary><c>_tokens(value)</c>: the lower-cased, comma-separated tokens of a header value.</summary>
    public static HashSet<string> HeaderTokens(string? value) =>
        [.. (value ?? string.Empty).Split(',').Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0)];

    /// <summary>
    /// The checks' <c>_await_preflight</c>, <c>_await_cors_grant</c> and kin: repeats a raw request every
    /// 2 seconds until <paramref name="done"/> holds of its answer (a CORS change propagates); fails with
    /// <paramref name="failure"/>'s message, given the last answer and the budget, once the budget (at most
    /// <paramref name="budgetSeconds"/>, and at most a <paramref name="share"/>th of the time left) runs out.
    /// </summary>
    public static async Task<(int Status, Dictionary<string, string> Headers, byte[] Body)> AwaitAnswer(
        FidelityContext ctx,
        string method,
        string url,
        IReadOnlyDictionary<string, string> headers,
        Func<(int Status, Dictionary<string, string> Headers, byte[] Body), bool> done,
        Func<(int Status, Dictionary<string, string> Headers, byte[] Body), double, string> failure,
        double budgetSeconds = 40.0,
        double share = 3.0)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(failure);
        budgetSeconds = Math.Min(budgetSeconds, ctx.TimeLeftSeconds / share);
        return await PollUntil(
            () => Send(ctx, method, url, headers),
            done,
            TimeSpan.FromSeconds(budgetSeconds),
            TimeSpan.FromSeconds(2),
            last => failure(last, budgetSeconds));
    }
}
