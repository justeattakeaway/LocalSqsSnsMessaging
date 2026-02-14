using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Amazon.SQS.Model;
using Microsoft.AspNetCore.Http;

namespace LocalSqsSnsMessaging.Server;

internal static class DashboardApi
{
    public static IResult GetState(InMemoryAwsBus bus)
    {
        return Results.Json(BuildState(bus), DashboardJsonContext.Default.BusState);
    }

    public static IResult StreamState(InMemoryAwsBus bus, CancellationToken cancellationToken)
    {
        return TypedResults.ServerSentEvents(GenerateStateEvents(bus, cancellationToken), eventType: "state");
    }

    public static IResult GetQueueMessages(InMemoryAwsBus bus, string queueName)
    {
        if (!bus.Queues.TryGetValue(queueName, out var queue))
        {
            return Results.NotFound("Queue not found");
        }

        var result = new QueueMessages
        {
            QueueName = queue.Name,
            PendingMessages = PeekChannelMessages(queue.Messages),
            InFlightMessages = queue.InFlightMessages.Values
                .Select(pair => ToMessageInfo(pair.Item1, inFlight: true))
                .ToList()
        };

        // For FIFO queues, also include messages from message groups
        if (queue.IsFifo)
        {
            foreach (var group in queue.MessageGroups)
            {
                foreach (var msg in group.Value)
                {
                    result.PendingMessages.Add(ToMessageInfo(msg, inFlight: false, messageGroupId: group.Key));
                }
            }
        }

        return Results.Json(result, DashboardJsonContext.Default.QueueMessages);
    }

    private static async IAsyncEnumerable<string> GenerateStateEvents(
        InMemoryAwsBus bus,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var lastJson = "";

        while (!cancellationToken.IsCancellationRequested)
        {
            var state = BuildState(bus);
            var json = JsonSerializer.Serialize(state, DashboardJsonContext.Default.BusState);

            if (json != lastJson)
            {
                lastJson = json;
                yield return json;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    private static BusState BuildState(InMemoryAwsBus bus)
    {
        return new BusState
        {
            Queues = bus.Queues.Values.Select(q => new QueueInfo
            {
                Name = q.Name,
                Arn = q.Arn,
                Url = q.Url,
                IsFifo = q.IsFifo,
                MessagesAvailable = q.Messages.Reader.Count,
                MessagesInFlight = q.InFlightMessages.Count,
                VisibilityTimeoutSeconds = (int)q.VisibilityTimeout.TotalSeconds,
                HasDeadLetterQueue = q.ErrorQueue is not null,
                DeadLetterQueueName = q.ErrorQueue?.Name,
                MaxReceiveCount = q.MaxReceiveCount
            }).OrderBy(q => q.Name).ToList(),

            Topics = bus.Topics.Values.Select(t => new TopicInfo
            {
                Name = t.Name,
                Arn = t.Arn
            }).OrderBy(t => t.Name).ToList(),

            Subscriptions = bus.Subscriptions.Values.Select(s => new SubscriptionInfo
            {
                SubscriptionArn = s.SubscriptionArn,
                TopicArn = s.TopicArn,
                Endpoint = s.EndPoint,
                Protocol = s.Protocol,
                Raw = s.Raw,
                FilterPolicy = string.IsNullOrEmpty(s.FilterPolicy) ? null : s.FilterPolicy
            }).ToList(),

            RecentOperations = bus.UsageTrackingEnabled
                ? bus.UsageTracker.Operations
                    .OrderByDescending(o => o.Timestamp)
                    .Take(200)
                    .Select(o => new OperationInfo
                    {
                        Service = o.Service,
                        Action = o.Action,
                        ResourceArn = o.ResourceArn,
                        Timestamp = o.Timestamp,
                        Success = o.Success
                    })
                    .ToList()
                : null
        };
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Peek is best-effort; returns empty on failure")]
    private static List<MessageInfo> PeekChannelMessages(Channel<Message> channel)
    {
        var itemsField = channel.GetType().GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance);

        if (itemsField?.GetValue(channel) is ConcurrentQueue<Message> queue)
        {
            return queue.Select(m => ToMessageInfo(m, inFlight: false)).ToList();
        }

        return [];
    }

    private static MessageInfo ToMessageInfo(Message msg, bool inFlight, string? messageGroupId = null)
    {
        return new MessageInfo
        {
            MessageId = msg.MessageId ?? Guid.NewGuid().ToString(),
            Body = msg.Body,
            InFlight = inFlight,
            MessageGroupId = messageGroupId,
            Attributes = msg.Attributes is { Count: > 0 } ? msg.Attributes : null,
            MessageAttributes = msg.MessageAttributes is { Count: > 0 }
                ? msg.MessageAttributes.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.DataType == "Number" ? kvp.Value.StringValue : kvp.Value.StringValue)
                : null
        };
    }

    // DTOs

    internal sealed class BusState
    {
        public required List<QueueInfo> Queues { get; init; }
        public required List<TopicInfo> Topics { get; init; }
        public required List<SubscriptionInfo> Subscriptions { get; init; }
        public List<OperationInfo>? RecentOperations { get; init; }
    }

    internal sealed class QueueInfo
    {
        public required string Name { get; init; }
        public required string Arn { get; init; }
        public required string Url { get; init; }
        public required bool IsFifo { get; init; }
        public required int MessagesAvailable { get; init; }
        public required int MessagesInFlight { get; init; }
        public required int VisibilityTimeoutSeconds { get; init; }
        public required bool HasDeadLetterQueue { get; init; }
        public string? DeadLetterQueueName { get; init; }
        public int? MaxReceiveCount { get; init; }
    }

    internal sealed class TopicInfo
    {
        public required string Name { get; init; }
        public required string Arn { get; init; }
    }

    internal sealed class SubscriptionInfo
    {
        public required string SubscriptionArn { get; init; }
        public required string TopicArn { get; init; }
        public required string Endpoint { get; init; }
        public required string Protocol { get; init; }
        public required bool Raw { get; init; }
        public string? FilterPolicy { get; init; }
    }

    internal sealed class QueueMessages
    {
        public required string QueueName { get; init; }
        public required List<MessageInfo> PendingMessages { get; set; }
        public required List<MessageInfo> InFlightMessages { get; init; }
    }

    internal sealed class MessageInfo
    {
        public required string MessageId { get; init; }
        public required string Body { get; init; }
        public required bool InFlight { get; init; }
        public string? MessageGroupId { get; init; }
        public Dictionary<string, string>? Attributes { get; init; }
        public Dictionary<string, string>? MessageAttributes { get; init; }
    }

    internal sealed class OperationInfo
    {
        public required string Service { get; init; }
        public required string Action { get; init; }
        public string? ResourceArn { get; init; }
        public required DateTimeOffset Timestamp { get; init; }
        public required bool Success { get; init; }
    }
}
