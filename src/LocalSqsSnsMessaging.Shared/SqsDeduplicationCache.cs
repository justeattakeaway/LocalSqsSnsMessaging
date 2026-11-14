using System.Security.Cryptography;
using System.Text;

namespace LocalSqsSnsMessaging;

/// <summary>
/// The MessageDeduplicationIds a FIFO queue (or one of its message groups) has accepted. As in SQS,
/// an id is held for the five-minute deduplication interval from when it was first accepted,
/// whether or not its message has since been received or deleted; only <c>PurgeQueue</c> forgets
/// it sooner.
/// </summary>
internal sealed class SqsDeduplicationCache
{
    /// <summary>SQS's deduplication interval.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private const int SweepEvery = 256;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _claimsSinceSweep;

    /// <summary>The message that claimed a deduplication id, and when.</summary>
    public readonly record struct Entry(string MessageId, string? SequenceNumber, DateTimeOffset AcceptedAt);

    /// <summary>
    /// Claims <paramref name="deduplicationId"/> for a new message, unless a message accepted
    /// within the interval holds it.
    /// </summary>
    /// <returns><see langword="true"/> when claimed; otherwise <paramref name="existing"/> is the holder.</returns>
    public bool TryClaim(string deduplicationId, string messageId, string? sequenceNumber, DateTimeOffset now, out Entry existing)
    {
        lock (_entries)
        {
            if (_entries.TryGetValue(deduplicationId, out existing) && now - existing.AcceptedAt < Interval)
            {
                return false;
            }

            _entries[deduplicationId] = new Entry(messageId, sequenceNumber, now);
            if (++_claimsSinceSweep >= SweepEvery)
            {
                _claimsSinceSweep = 0;
                foreach (var expired in _entries.Where(pair => now - pair.Value.AcceptedAt >= Interval).Select(pair => pair.Key).ToList())
                {
                    _entries.Remove(expired);
                }
            }

            existing = default;
            return true;
        }
    }

    public void Clear()
    {
        lock (_entries)
        {
            _entries.Clear();
        }
    }

    /// <summary>The deduplication id SQS derives for content-based deduplication: the SHA-256 of the body, in hex.</summary>
    public static string ContentDeduplicationId(string messageBody)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(messageBody));
#pragma warning disable CA1308 // SQS reports the digest in lower case.
        return Convert.ToHexString(hash).ToLowerInvariant();
#pragma warning restore CA1308
    }
}
