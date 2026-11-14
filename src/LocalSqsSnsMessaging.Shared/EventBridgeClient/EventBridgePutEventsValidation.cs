using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

/// <summary>
/// PutEvents validation: the request-wide limits that refuse a request whole, and the per-entry
/// checks that fail one entry in place.
/// </summary>
internal static class EventBridgePutEventsValidation
{
    /// <summary>The most entries one PutEvents request may carry.</summary>
    private const int MaxEntries = 10;

    /// <summary>The most bytes the entries of one PutEvents request may total, as <see cref="EntrySize"/> counts them.</summary>
    private const int MaxRequestSize = 1024 * 1024;

    /// <summary>What an entry's <c>Time</c> adds to its size.</summary>
    private const int TimeSize = 14;

    /// <summary>
    /// Refuses a request whole when it has no entries or more than 10, or when its entries total
    /// more than 1 MiB.
    /// </summary>
    public static void ValidateRequest(List<PutEventsRequestEntry> entries)
    {
        if (entries.Count is 0 or > MaxEntries)
        {
            var constraint = entries.Count == 0
                ? "Member must have length greater than or equal to 1"
                : $"Member must have length less than or equal to {MaxEntries}";
            throw RequestError(
                $"1 validation error detected: Value '[{string.Join(", ", entries.Select(DescribeEntry))}]' at 'entries' " +
                $"failed to satisfy constraint: {constraint}");
        }

        if (entries.Sum(EntrySize) > MaxRequestSize)
        {
            throw RequestError("Total size of the entries in the request is over the limit.");
        }
    }

    /// <summary>
    /// An entry's size as EventBridge counts it against the request limit: the UTF-8 bytes of
    /// Source, DetailType, Detail and each Resources entry, plus 14 bytes when Time is set.
    /// See https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-putevents.html
    /// </summary>
    private static long EntrySize(PutEventsRequestEntry entry)
    {
        long size = entry.Time is null ? 0 : TimeSize;
        size += Utf8Length(entry.Source) + Utf8Length(entry.DetailType) + Utf8Length(entry.Detail);
        foreach (var resource in entry.Resources ?? [])
        {
            size += Utf8Length(resource);
        }
        return size;
    }

    private static int Utf8Length(string? value) => value is null ? 0 : Encoding.UTF8.GetByteCount(value);

    /// <summary>
    /// Whether an entry is valid. A valid entry's Detail is parsed into <paramref name="detail"/>;
    /// an invalid one gets the in-place <paramref name="failure"/> PutEvents returns for it.
    /// </summary>
    public static bool TryValidateEntry(
        PutEventsRequestEntry entry,
        [NotNullWhen(true)] out JsonObject? detail,
        [NotNullWhen(false)] out PutEventsResultEntry? failure)
    {
        detail = null;
        failure = MissingArgument(entry.Source, "Source")
                  ?? MissingArgument(entry.DetailType, "DetailType")
                  ?? MissingArgument(entry.Detail, "Detail");
        if (failure is not null)
        {
            return false;
        }

        detail = EventBridgeEnvelope.ParseDetail(entry.Detail!);
        if (detail is null)
        {
            failure = new PutEventsResultEntry { ErrorCode = "MalformedDetail", ErrorMessage = "Detail is malformed." };
            return false;
        }
        return true;
    }

    private static PutEventsResultEntry? MissingArgument(string? value, string parameter) =>
        string.IsNullOrEmpty(value)
            ? new PutEventsResultEntry
            {
                ErrorCode = "InvalidArgument",
                ErrorMessage = $"Parameter {parameter} is not valid. Reason: {parameter} is a required argument."
            }
            : null;

    /// <summary>An entry as EventBridge echoes it in a validation message.</summary>
    private static string DescribeEntry(PutEventsRequestEntry entry)
    {
        var time = entry.Time is { } t
            ? t.ToUniversalTime().ToString("ddd MMM dd HH:mm:ss 'UTC' yyyy", CultureInfo.InvariantCulture)
            : "null";
        var resources = entry.Resources is { } r ? $"[{string.Join(", ", r)}]" : "null";
        return $"PutEventsRequestEntry(time={time}, source={entry.Source ?? "null"}, resources={resources}, " +
               $"detailType={entry.DetailType ?? "null"}, detail={entry.Detail ?? "null"}, " +
               $"eventBusName={entry.EventBusName ?? "null"}, traceHeader={entry.TraceHeader ?? "null"})";
    }

    private static AwsServiceException RequestError(string message) =>
        new(message) { ErrorCode = "ValidationException" };
}
