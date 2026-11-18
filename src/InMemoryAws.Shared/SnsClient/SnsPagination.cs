namespace InMemoryAws;

/// <summary>
/// Pages SNS list results the way SNS does: at most 100 per page, in a stable order, with a
/// <c>NextToken</c> that only SNS can have issued. A token remembers the last key it returned, so
/// a walk neither repeats nor skips entries when others are added or removed between pages.
/// </summary>
internal static class SnsPagination
{
    /// <summary>The most entries an SNS list operation returns per page.</summary>
    public const int PageSize = 100;

    private const string TokenPrefix = "lsm-sns-page:";

    /// <summary>
    /// The page of <paramref name="keys"/> that <paramref name="nextToken"/> asks for, and the token
    /// for the page after it. A token SNS didn't issue is refused with <c>InvalidParameter</c>.
    /// </summary>
    /// <param name="keys">Every key there is to list.</param>
    /// <param name="nextToken">The caller's <c>NextToken</c>, if any.</param>
    /// <param name="pageSize">The most keys a page holds.</param>
    /// <param name="invalidTokenMessage">The refusal for a token SNS didn't issue; operations word it differently.</param>
    public static (List<string> Items, string? NextToken) Page(IEnumerable<string> keys, string? nextToken,
        int pageSize = PageSize, string invalidTokenMessage = "Invalid parameter: NextToken")
    {
        var after = string.IsNullOrEmpty(nextToken) ? null : DecodeToken(nextToken!, invalidTokenMessage);
        var ordered = keys
            .Where(k => after is null || string.CompareOrdinal(k, after) > 0)
            .Order(StringComparer.Ordinal)
            .Take(pageSize + 1)
            .ToList();

        if (ordered.Count <= pageSize)
        {
            return (ordered, null);
        }

        ordered.RemoveAt(pageSize);
        return (ordered, EncodeToken(ordered[^1]));
    }

    private static string EncodeToken(string lastKey) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(TokenPrefix + lastKey));

    private static string DecodeToken(string token, string invalidTokenMessage)
    {
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            if (decoded.StartsWith(TokenPrefix, StringComparison.Ordinal))
            {
                return decoded[TokenPrefix.Length..];
            }
        }
        catch (FormatException)
        {
        }

        throw new InternalInvalidParameterException(invalidTokenMessage);
    }
}
