#pragma warning disable CS8600, CS8601, CS8602, CS8604, CS8619 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using System.Collections.Concurrent;
using InMemoryAws.Sqs.Model;

namespace InMemoryAws;

internal sealed partial class InternalSqsClient
{
    private readonly InMemoryAwsCloud _cloud;

    /// <summary>The region this client serves: it sees, creates and wires only this region's queues.</summary>
    private readonly string _region;

    private readonly AwsRegionResources _resources;

    private const int MaxMessageSize = 1_048_576; // 1MB

    /// <param name="cloud">The cloud holding the queues.</param>
    /// <param name="region">
    /// The region the request was made in; <see langword="null"/> for the cloud's
    /// <see cref="InMemoryAwsCloud.CurrentRegion"/>.
    /// </param>
    internal InternalSqsClient(InMemoryAwsCloud cloud, string? region = null)
    {
        _cloud = cloud;
        _resources = cloud.Region(region);
        _region = _resources.Region;
    }

    /// <summary>The queues in this client's region, keyed by name.</summary>
    private ConcurrentDictionary<string, SqsQueueResource> Queues => _resources.Queues;

    /// <summary>The queue an ARN names, if it is one of this region's queues.</summary>
    private SqsQueueResource? FindQueueByArn(string? arn) =>
        _cloud.TryGetQueueByArn(arn, out var queue) && string.Equals(queue.Region, _region, StringComparison.Ordinal)
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
