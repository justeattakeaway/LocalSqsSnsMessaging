using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using LocalSqsSnsMessaging.Sns.Model;
using LocalSqsSnsMessaging.Sqs.Model;
using Microsoft.AspNetCore.Http;

namespace LocalSqsSnsMessaging.Server;

internal static class DashboardApi
{
    public static IResult GetState(BusRegistry registry, string? account)
    {
        var bus = ResolveBus(registry, account);
        var currentAccount = account ?? registry.DefaultAccountId;
        return Results.Json(BuildState(bus, registry.AccountIds, currentAccount), DashboardJsonContext.Default.BusState);
    }

    public static IResult StreamState(BusRegistry registry, string? account, CancellationToken cancellationToken)
    {
        return TypedResults.ServerSentEvents(GenerateStateEvents(registry, account, cancellationToken), eventType: "state");
    }

    public static IResult GetQueueMessages(BusRegistry registry, string? account, string queueName)
    {
        var bus = ResolveBus(registry, account);

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

    private static InMemoryAwsBus ResolveBus(BusRegistry registry, string? account)
    {
        if (!string.IsNullOrEmpty(account))
        {
            return registry.GetOrCreate(account);
        }

        return registry.DefaultBus;
    }

    private static async IAsyncEnumerable<string> GenerateStateEvents(
        BusRegistry registry,
        string? account,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var lastJson = "";

        while (!cancellationToken.IsCancellationRequested)
        {
            var bus = ResolveBus(registry, account);
            var currentAccount = account ?? registry.DefaultAccountId;
            var state = BuildState(bus, registry.AccountIds, currentAccount);
            var json = JsonSerializer.Serialize(state, DashboardJsonContext.Default.BusState);

            if (json != lastJson)
            {
                lastJson = json;
                yield return json;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    private static BusState BuildState(InMemoryAwsBus bus, List<string> accounts, string currentAccount)
    {
        return new BusState
        {
            Accounts = accounts,
            CurrentAccount = currentAccount,

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

    public static IResult DeleteMessage(BusRegistry registry, string? account, string queueName, string messageId)
    {
        var bus = ResolveBus(registry, account);

        if (!bus.Queues.TryGetValue(queueName, out var queue))
        {
            return Results.NotFound("Queue not found");
        }

        // Try removing from in-flight messages
        foreach (var kvp in queue.InFlightMessages)
        {
            if (kvp.Value.Item1.MessageId == messageId)
            {
                if (queue.InFlightMessages.TryRemove(kvp.Key, out var removed))
                {
                    removed.Item2.Dispose();
                    bus.RecordOperation("Dashboard", "DeleteMessage", queue.Arn);
                    return Results.NoContent();
                }
            }
        }

        // Try removing from pending messages (drain and re-enqueue without the target)
        if (RemoveFromChannel(queue.Messages, messageId))
        {
            bus.RecordOperation("Dashboard", "DeleteMessage", queue.Arn);
            return Results.NoContent();
        }

        // Try removing from FIFO message groups
        if (queue.IsFifo)
        {
            foreach (var group in queue.MessageGroups)
            {
                var original = group.Value;
                var filtered = new ConcurrentQueue<Message>(original.Where(m => m.MessageId != messageId));
                if (filtered.Count < original.Count)
                {
                    queue.MessageGroups.TryUpdate(group.Key, filtered, original);
                    bus.RecordOperation("Dashboard", "DeleteMessage", queue.Arn);
                    return Results.NoContent();
                }
            }
        }

        return Results.NotFound("Message not found");
    }

    public static async Task<IResult> PublishToTopic(BusRegistry registry, string? account, string topicName, HttpContext ctx)
    {
        var bus = ResolveBus(registry, account);

        if (!bus.Topics.TryGetValue(topicName, out var topic))
        {
            return Results.NotFound("Topic not found");
        }

        var body = await ctx.Request.ReadFromJsonAsync(DashboardJsonContext.Default.PublishTopicRequest).ConfigureAwait(false);
        if (body is null || string.IsNullOrEmpty(body.Message))
        {
            return Results.BadRequest("Message is required");
        }

        var snsClient = new InternalSnsClient(bus);
        var response = await snsClient.PublishAsync(new PublishRequest
        {
            TopicArn = topic.Arn,
            Message = body.Message,
            Subject = body.Subject
        }).ConfigureAwait(false);

        return Results.Json(
            new PublishTopicResponse { MessageId = response.MessageId! },
            DashboardJsonContext.Default.PublishTopicResponse);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Channel internals accessed for dashboard peek/delete")]
    private static bool RemoveFromChannel(Channel<Message> channel, string messageId)
    {
        var itemsField = channel.GetType().GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance);

        if (itemsField?.GetValue(channel) is ConcurrentQueue<Message> queue)
        {
            var count = queue.Count;
            for (var i = 0; i < count; i++)
            {
                if (queue.TryDequeue(out var msg))
                {
                    if (msg.MessageId == messageId)
                    {
                        return true;
                    }

                    queue.Enqueue(msg);
                }
            }
        }

        return false;
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
        // Assign a stable MessageId if the message doesn't have one (e.g. SNS-delivered messages)
        msg.MessageId ??= Guid.NewGuid().ToString();
        return new MessageInfo
        {
            MessageId = msg.MessageId,
            Body = msg.Body!,
            InFlight = inFlight,
            MessageGroupId = messageGroupId,
            Attributes = msg.Attributes is { Count: > 0 } ? msg.Attributes : null,
            MessageAttributes = msg.MessageAttributes is { Count: > 0 }
                ? msg.MessageAttributes.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.StringValue ?? "")
                : null
        };
    }

    // DTOs

    internal sealed class BusState
    {
        public required List<string> Accounts { get; init; }
        public required string CurrentAccount { get; init; }
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

    internal sealed class PublishTopicRequest
    {
        public required string Message { get; init; }
        public string? Subject { get; init; }
    }

    internal sealed class PublishTopicResponse
    {
        public required string MessageId { get; init; }
    }
}
