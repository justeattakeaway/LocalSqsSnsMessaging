using System.Globalization;
using System.Text;

namespace LocalSqsSnsMessaging;

/// <summary>
/// One page of an EventBridge list operation (ListRules, ListTargetsByRule, ListRuleNamesByTarget):
/// at most <c>Limit</c> items (every item when it is omitted), and a <c>NextToken</c> only when more
/// items follow, so a caller's paging loop ends on the last page.
/// </summary>
/// <remarks>
/// A token encodes the offset of the next item. A token this class did not issue is refused with
/// <c>ValidationException</c> "Parameter NextToken is not valid.", as EventBridge refuses one.
/// </remarks>
internal static class EventBridgePage
{
    private const string TokenPrefix = "eventbridge-page:";

    /// <summary>The largest <c>Limit</c> EventBridge accepts.</summary>
    private const int MaxLimit = 100;

    /// <summary>Takes the page of <paramref name="items"/> that <paramref name="nextToken"/> and <paramref name="limit"/> select.</summary>
    public static List<T> Take<T>(IReadOnlyList<T> items, int? limit, string? nextToken, out string? newNextToken)
    {
        if (limit is < 1 or > MaxLimit)
        {
            throw new InternalValidationException(
                limit < 1
                    ? $"1 validation error detected: Value '{limit}' at 'limit' failed to satisfy constraint: Member must have value greater than or equal to 1"
                    : $"1 validation error detected: Value '{limit}' at 'limit' failed to satisfy constraint: Member must have value less than or equal to {MaxLimit}");
        }

        var start = string.IsNullOrEmpty(nextToken) ? 0 : DecodeToken(nextToken!);
        var count = Math.Max(0, Math.Min(limit ?? int.MaxValue, items.Count - start));
        var page = new List<T>(count);
        for (var i = start; i < start + count; i++)
        {
            page.Add(items[i]);
        }

        newNextToken = start + count < items.Count ? EncodeToken(start + count) : null;
        return page;
    }

    private static string EncodeToken(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(TokenPrefix + offset.ToString(CultureInfo.InvariantCulture)));

    private static int DecodeToken(string token)
    {
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token));
        }
        catch (FormatException)
        {
            throw InvalidToken();
        }

        if (!decoded.StartsWith(TokenPrefix, StringComparison.Ordinal)
            || !int.TryParse(decoded[TokenPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
        {
            throw InvalidToken();
        }

        return offset;
    }

    private static InternalValidationException InvalidToken() => new("Parameter NextToken is not valid.");
}
