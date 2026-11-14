using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LocalSqsSnsMessaging.Sns.Model;
using LocalSqsSnsMessaging.Sqs.Model;
using Microsoft.AspNetCore.Http;
using StartMessageMoveTaskRequest = LocalSqsSnsMessaging.Sqs.Model.StartMessageMoveTaskRequest;
using CancelMessageMoveTaskRequest = LocalSqsSnsMessaging.Sqs.Model.CancelMessageMoveTaskRequest;

namespace LocalSqsSnsMessaging.Server;

internal static class DashboardApi
{
    public static IResult GetState(BusRegistry registry, string? account, string? region)
    {
        var bus = ResolveBus(registry, account);
        var currentAccount = account ?? registry.DefaultAccountId;
        return Results.Json(BuildState(bus, ResolveRegion(bus, region), registry.AccountIds, currentAccount), DashboardJsonContext.Default.BusState);
    }

    public static IResult StreamState(BusRegistry registry, string? account, string? region, CancellationToken cancellationToken)
    {
        return TypedResults.ServerSentEvents(GenerateStateEvents(registry, account, region, cancellationToken), eventType: "state");
    }

    public static IResult GetQueueMessages(BusRegistry registry, string? account, string? region, string queueName)
    {
        var bus = ResolveBus(registry, account);

        if (!ResolveRegion(bus, region).Queues.TryGetValue(queueName, out var queue))
        {
            return Results.NotFound("Queue not found");
        }

        var result = new QueueMessages
        {
            QueueName = queue.Name,
            PendingMessages = queue.Messages.Snapshot()
                .Select(m => ToMessageInfo(m, inFlight: false)).ToList(),
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

    /// <summary>The resources of <paramref name="region"/> in <paramref name="bus"/>; the bus's own region when none is named.</summary>
    private static AwsRegionResources ResolveRegion(InMemoryAwsBus bus, string? region) => bus.Region(region);

    /// <summary>
    /// The regions that hold anything, and always the bus's own and the one being viewed (so the
    /// dashboard can still switch away from a region that has emptied), in order.
    /// </summary>
    private static List<string> RegionsInUse(InMemoryAwsBus bus, string viewedRegion) =>
        bus.Regions
            .Where(r => !r.Queues.IsEmpty || !r.Topics.IsEmpty || !r.EventBuses.IsEmpty)
            .Select(r => r.Region)
            .Append(bus.CurrentRegion)
            .Append(viewedRegion)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static async IAsyncEnumerable<string> GenerateStateEvents(
        BusRegistry registry,
        string? account,
        string? region,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var lastJson = "";

        while (!cancellationToken.IsCancellationRequested)
        {
            var bus = ResolveBus(registry, account);
            var currentAccount = account ?? registry.DefaultAccountId;
            var state = BuildState(bus, ResolveRegion(bus, region), registry.AccountIds, currentAccount);
            var json = JsonSerializer.Serialize(state, DashboardJsonContext.Default.BusState);

            if (json != lastJson)
            {
                lastJson = json;
                yield return json;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    private static BusState BuildState(InMemoryAwsBus bus, AwsRegionResources resources, List<string> accounts, string currentAccount)
    {
        return new BusState
        {
            Accounts = accounts,
            CurrentAccount = currentAccount,
            Regions = RegionsInUse(bus, resources.Region),
            CurrentRegion = resources.Region,

            Queues = resources.Queues.Values.Select(q => new QueueInfo
            {
                Name = q.Name,
                Arn = q.Arn,
                Url = q.Url,
                IsFifo = q.IsFifo,
                MessagesAvailable = q.Messages.Count,
                MessagesInFlight = q.InFlightMessages.Count,
                VisibilityTimeoutSeconds = (int)q.VisibilityTimeout.TotalSeconds,
                HasDeadLetterQueue = q.ErrorQueue is not null,
                DeadLetterQueueName = q.ErrorQueue?.Name,
                MaxReceiveCount = q.MaxReceiveCount
            }).OrderBy(q => q.Name).ToList(),

            Topics = resources.Topics.Values.Select(t => new TopicInfo
            {
                Name = t.Name,
                Arn = t.Arn
            }).OrderBy(t => t.Name).ToList(),

            Subscriptions = resources.Subscriptions.Values.Select(s => new SubscriptionInfo
            {
                SubscriptionArn = s.SubscriptionArn,
                TopicArn = s.TopicArn,
                Endpoint = s.EndPoint,
                Protocol = s.Protocol,
                Raw = s.Raw,
                FilterPolicy = string.IsNullOrEmpty(s.FilterPolicy) ? null : s.FilterPolicy
            }).ToList(),

            MoveTasks = bus.MoveTasks.Values
                .Where(t => string.Equals(InMemoryAwsBus.ArnRegion(t.SourceQueue.Arn), resources.Region, StringComparison.Ordinal))
                .Select(t => new MoveTaskInfo
            {
                TaskHandle = t.TaskHandle,
                SourceArn = t.SourceQueue.Arn,
                DestinationArn = t.DestinationQueue?.Arn,
                Status = t.Status,
                ApproximateNumberOfMessagesMoved = t.ApproximateNumberOfMessagesMoved,
                ApproximateNumberOfMessagesToMove = t.ApproximateNumberOfMessagesToMove
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

    public static IResult DeleteMessage(BusRegistry registry, string? account, string? region, string queueName, string messageId)
    {
        var bus = ResolveBus(registry, account);

        if (!ResolveRegion(bus, region).Queues.TryGetValue(queueName, out var queue))
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
        if (queue.Messages.TryRemove(messageId))
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

    public static async Task<IResult> StartRedrive(BusRegistry registry, string? account, string? region, string queueName)
    {
        var bus = ResolveBus(registry, account);

        if (!ResolveRegion(bus, region).Queues.TryGetValue(queueName, out var queue))
        {
            return Results.NotFound("Queue not found");
        }

        var sqsClient = new InternalSqsClient(bus, region);
        try
        {
            var response = await sqsClient.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
            {
                SourceArn = queue.Arn
            }).ConfigureAwait(false);

            return Results.Json(
                new RedriveResponse { TaskHandle = response.TaskHandle! },
                DashboardJsonContext.Default.RedriveResponse);
        }
        catch (AwsServiceException ex)
        {
            return Results.BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(ex.Message);
        }
    }

    public static async Task<IResult> CancelRedrive(BusRegistry registry, string? account, string? region, string taskHandle)
    {
        var bus = ResolveBus(registry, account);

        var sqsClient = new InternalSqsClient(bus, region);
        try
        {
            await sqsClient.CancelMessageMoveTaskAsync(new CancelMessageMoveTaskRequest
            {
                TaskHandle = taskHandle
            }).ConfigureAwait(false);

            return Results.NoContent();
        }
        catch (AwsServiceException ex)
        {
            return Results.BadRequest(ex.Message);
        }
    }

    /// <summary>The message group the dashboard publishes to a FIFO topic in.</summary>
    private const string DashboardMessageGroupId = "dashboard";

    public static async Task<IResult> PublishToTopic(BusRegistry registry, string? account, string? region, string topicName, HttpContext ctx)
    {
        var bus = ResolveBus(registry, account);

        if (!ResolveRegion(bus, region).Topics.TryGetValue(topicName, out var topic))
        {
            return Results.NotFound("Topic not found");
        }

        var body = await ctx.Request.ReadFromJsonAsync(DashboardJsonContext.Default.PublishTopicRequest).ConfigureAwait(false);
        if (body is null || string.IsNullOrEmpty(body.Message))
        {
            return Results.BadRequest("Message is required");
        }

        var snsClient = new InternalSnsClient(bus, region);
        PublishResponse response;
        try
        {
            response = await snsClient.PublishAsync(new PublishRequest
            {
                TopicArn = topic.Arn,
                Message = body.Message,
                Subject = body.Subject,
                // A FIFO topic needs a message group, and a deduplication id unless it deduplicates by
                // content: each publish from the dashboard is a distinct message in one group.
                MessageGroupId = topic.IsFifo ? DashboardMessageGroupId : null,
                MessageDeduplicationId = topic.IsFifo ? Guid.NewGuid().ToString("N") : null,
            }).ConfigureAwait(false);
        }
        catch (AwsServiceException ex)
        {
            return Results.BadRequest(ex.Message);
        }

        return Results.Json(
            new PublishTopicResponse { MessageId = response.MessageId! },
            DashboardJsonContext.Default.PublishTopicResponse);
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
        public required List<string> Regions { get; init; }
        public required string CurrentRegion { get; init; }
        public required List<QueueInfo> Queues { get; init; }
        public required List<TopicInfo> Topics { get; init; }
        public required List<SubscriptionInfo> Subscriptions { get; init; }
        public required List<MoveTaskInfo> MoveTasks { get; init; }
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

    internal sealed class MoveTaskInfo
    {
        public required string TaskHandle { get; init; }
        public required string SourceArn { get; init; }
        public string? DestinationArn { get; init; }
        public required string Status { get; init; }
        public required int ApproximateNumberOfMessagesMoved { get; init; }
        public required int ApproximateNumberOfMessagesToMove { get; init; }
    }

    internal sealed class RedriveResponse
    {
        public required string TaskHandle { get; init; }
    }
}
