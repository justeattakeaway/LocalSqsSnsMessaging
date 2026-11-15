#pragma warning disable CS8600, CS8601, CS8602, CS8604, CS8619 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using System.Collections.Concurrent;
using LocalSqsSnsMessaging.Sqs.Model;

namespace LocalSqsSnsMessaging;

internal sealed partial class InternalSqsClient
{
    private readonly InMemoryAwsBus _bus;

    /// <summary>The region this client serves: it sees, creates and wires only this region's queues.</summary>
    private readonly string _region;

    private readonly AwsRegionResources _resources;

    private const int MaxMessageSize = 1_048_576; // 1MB

    /// <param name="bus">The bus holding the queues.</param>
    /// <param name="region">
    /// The region the request was made in; <see langword="null"/> for the bus's
    /// <see cref="InMemoryAwsBus.CurrentRegion"/>.
    /// </param>
    internal InternalSqsClient(InMemoryAwsBus bus, string? region = null)
    {
        _bus = bus;
        _resources = bus.Region(region);
        _region = _resources.Region;
    }

    /// <summary>The queues in this client's region, keyed by name.</summary>
    private ConcurrentDictionary<string, SqsQueueResource> Queues => _resources.Queues;

    /// <summary>The queue an ARN names, if it is one of this region's queues.</summary>
    private SqsQueueResource? FindQueueByArn(string? arn) =>
        _bus.TryGetQueueByArn(arn, out var queue) && string.Equals(queue.Region, _region, StringComparison.Ordinal)
            ? queue
            : null;

    private static string GetQueueNameFromUrl(string queueUrl)
    {
        return queueUrl.Split('/').Last();
    }

    private static string GetQueueNameFromArn(string queueArn)
    {
        var indexOfLastColon = queueArn.LastIndexOf(':');
        if (indexOfLastColon == -1)
        {
            throw new ArgumentException("ARN malformed", nameof(queueArn));
        }
        return queueArn[(indexOfLastColon+1) ..];
    }
}
