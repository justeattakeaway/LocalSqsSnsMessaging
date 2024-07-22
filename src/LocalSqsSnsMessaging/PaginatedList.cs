namespace LocalSqsSnsMessaging;

internal sealed class PaginatedList<T>
{
    private readonly IList<T> _items;

    public PaginatedList(IEnumerable<T> items)
    {
        _items = items.ToList();
    }

    /// <summary>
    /// Gets a paginated and optionally filtered list. Useful for AWS APIs with paginated responses.
    /// </summary>
    /// <param name="tokenGenerator">Function to generate a token for each item</param>
    /// <param name="pageSize">Number of items per page</param>
    /// <param name="nextToken">Token for the next page</param>
    /// <param name="filter">Optional filter function</param>
    /// <returns>A tuple containing the items for the current page and the next page token</returns>
    public (List<T> Items, string? NextToken) GetPage(
        Func<T, string> tokenGenerator,
        int pageSize,
        string? nextToken = null,
        Func<T, bool>? filter = null)
    {
        ArgumentNullException.ThrowIfNull(tokenGenerator);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pageSize, 0, "Page size must be positive.");
        
        var query = filter != null ? _items.Where(filter).ToList() : _items;

        var startIndex = 0;
        if (!string.IsNullOrEmpty(nextToken))
        {
            startIndex = query.FindIndex(item => tokenGenerator(item) == nextToken);
            if (startIndex == -1) startIndex = 0;  // Token not found, start from beginning
        }

        var page = query.Skip(startIndex).Take(pageSize).ToList();

        string? newNextToken = null;
        if (startIndex + pageSize < query.Count)
        {
            newNextToken = tokenGenerator(query.ElementAt(startIndex + pageSize));
        }

        return (page, newNextToken);
    }
}

internal static class EnumerableExtensions
{
    public static int FindIndex<T>(this IEnumerable<T> source, Func<T, bool> predicate)
    {
        var index = 0;
        foreach (var item in source)
        {
            if (predicate(item)) return index;
            index++;
        }
        return -1;
    }
}