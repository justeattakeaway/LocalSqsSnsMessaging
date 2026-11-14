namespace LocalSqsSnsMessaging;

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
    public static (List<string> Items, string? NextToken) Page(IEnumerable<string> keys, string? nextToken)
    {
        var after = string.IsNullOrEmpty(nextToken) ? null : DecodeToken(nextToken!);
        var ordered = keys
            .Where(k => after is null || string.CompareOrdinal(k, after) > 0)
            .Order(StringComparer.Ordinal)
            .Take(PageSize + 1)
            .ToList();

        if (ordered.Count <= PageSize)
        {
            return (ordered, null);
        }

        ordered.RemoveAt(PageSize);
        return (ordered, EncodeToken(ordered[^1]));
    }

    private static string EncodeToken(string lastKey) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(TokenPrefix + lastKey));

    private static string DecodeToken(string token)
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

        throw new InternalInvalidParameterException("Invalid parameter: NextToken");
    }
}
