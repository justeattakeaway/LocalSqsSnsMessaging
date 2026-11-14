using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LocalSqsSnsMessaging.EventBridge.Model;
using SqsMessage = LocalSqsSnsMessaging.Sqs.Model.Message;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Routes event envelopes through a bus's rules and delivers matched events to SQS targets (the
/// only targets with an in-memory backend). <see cref="EventBridgeEnvelope"/> builds the envelope
/// and <see cref="EventBridgeTargetInput"/> renders what each target receives.
/// </summary>
internal static class EventBridgeEventDelivery
{
    /// <summary>Routes an event through the bus's rules and delivers to matched SQS targets.</summary>
    public static void Route(EventBusResource eventBus, EventBridgeEvent @event, InMemoryAwsBus bus)
    {
        foreach (var rule in eventBus.Rules.Values)
        {
            if (!rule.IsEnabled)
            {
                continue;
            }
            if (!EventBridgeEventPattern.Matches(rule.EventPattern, @event.Envelope))
            {
                continue;
            }

            foreach (var target in rule.Targets)
            {
                DeliverToTarget(target, rule, @event, bus);
            }
        }
    }

    private static void DeliverToTarget(Target target, RuleResource rule, EventBridgeEvent @event, InMemoryAwsBus bus)
    {
        var arn = target.Arn;

        // Only SQS targets have an in-memory backend; other target types are silently skipped.
        if (arn is null || !arn.StartsWith("arn:aws:sqs:", StringComparison.Ordinal))
        {
            return;
        }

        if (!bus.TryGetQueueByArn(arn, out var queue))
        {
            return;
        }

        var payload = EventBridgeTargetInput.Render(target, rule, @event);
        if (!payload.IsValidJson)
        {
            // Never sent: EventBridge dead-letters input that is not JSON (see EventBridgeTargetPayload).
            return;
        }

        var body = payload.Body;
        var message = new SqsMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            Body = body
        };
#pragma warning disable CA5351, CA1308
        message.MD5OfBody = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
#pragma warning restore CA5351, CA1308
        if (@event.TraceHeader is not null)
        {
            message.Attributes = new() { [InternalMessageSystemAttributeName.AWSTraceHeader] = @event.TraceHeader };
        }

        if (queue.IsFifo)
        {
            var groupId = EventBridgeTargetInput.ResolveMessageGroupId(target, @event.Envelope) ?? target.Id ?? "eventbridge";
            message.Attributes ??= [];
            message.Attributes["MessageGroupId"] = groupId;
            queue.MessageGroups.AddOrUpdate(groupId,
                _ => new ConcurrentQueue<SqsMessage>([message]),
                (_, q) =>
                {
                    q.Enqueue(message);
                    return q;
                });
        }
        else
        {
            queue.Messages.Enqueue(message);
        }
    }
}
