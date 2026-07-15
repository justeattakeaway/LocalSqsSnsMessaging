using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.EventBridge.Model;
using SqsMessage = LocalSqsSnsMessaging.Sqs.Model.Message;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Builds EventBridge event envelopes, routes them through a bus's rules, and delivers
/// matched events to SQS targets (the only targets with an in-memory backend).
/// </summary>
internal static class EventBridgeEventDelivery
{
    /// <summary>Builds the EventBridge event envelope for a PutEvents entry.</summary>
    public static JsonObject BuildEnvelope(PutEventsRequestEntry entry, string eventId, InMemoryAwsBus bus)
    {
        JsonNode? detail = null;
        if (!string.IsNullOrWhiteSpace(entry.Detail))
        {
            try
            {
                detail = JsonNode.Parse(entry.Detail!);
            }
            catch (JsonException)
            {
                detail = null;
            }
        }
        detail ??= new JsonObject();

        var time = entry.Time ?? bus.TimeProvider.GetUtcNow().UtcDateTime;

        var resources = new JsonArray();
        if (entry.Resources is not null)
        {
            foreach (var r in entry.Resources)
            {
                resources.Add((JsonNode?)r);
            }
        }

        return new JsonObject
        {
            ["version"] = "0",
            ["id"] = eventId,
            ["detail-type"] = entry.DetailType ?? string.Empty,
            ["source"] = entry.Source ?? string.Empty,
            ["account"] = bus.CurrentAccountId,
            ["time"] = time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["region"] = bus.CurrentRegion,
            ["resources"] = resources,
            ["detail"] = detail
        };
    }

    /// <summary>Routes an envelope through the bus's rules and delivers to matched SQS targets.</summary>
    public static void Route(EventBusResource eventBus, JsonObject envelope, InMemoryAwsBus bus)
    {
        foreach (var rule in eventBus.Rules.Values)
        {
            if (!rule.IsEnabled)
            {
                continue;
            }
            if (!EventBridgeEventPattern.Matches(rule.EventPattern, envelope))
            {
                continue;
            }

            foreach (var target in rule.Targets)
            {
                DeliverToTarget(target, envelope, bus);
            }
        }
    }

    private static void DeliverToTarget(Target target, JsonObject envelope, InMemoryAwsBus bus)
    {
        var arn = target.Arn;

        // Only SQS targets have an in-memory backend; other target types are silently skipped.
        if (arn is null || !arn.StartsWith("arn:aws:sqs:", StringComparison.Ordinal))
        {
            return;
        }

        var queueName = arn.Split(':')[^1];
        if (!bus.Queues.TryGetValue(queueName, out var queue))
        {
            return;
        }

        var body = ResolveInput(target, envelope);
        var message = new SqsMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            Body = body
        };
#pragma warning disable CA5351, CA1308
        message.MD5OfBody = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
#pragma warning restore CA5351, CA1308

        if (queue.IsFifo)
        {
            var groupId = target.SqsParameters?.MessageGroupId ?? target.Id ?? "eventbridge";
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
            queue.Messages.Writer.TryWrite(message);
        }
    }

    private static string ResolveInput(Target target, JsonObject envelope)
    {
        if (target.Input is not null)
        {
            return target.Input;
        }
        if (target.InputPath is not null)
        {
            return ApplyPath(envelope, target.InputPath) ?? "null";
        }
        if (target.InputTransformer is not null)
        {
            return ApplyTransformer(target.InputTransformer, envelope);
        }
        return envelope.ToJsonString();
    }

    private static string ApplyTransformer(InputTransformer transformer, JsonObject envelope)
    {
        var template = transformer.InputTemplate ?? string.Empty;
        if (transformer.InputPathsMap is not null)
        {
            foreach (var (name, path) in transformer.InputPathsMap)
            {
                var value = ApplyPath(envelope, path);
                template = template.Replace($"<{name}>", value is null ? string.Empty : Unquote(value), StringComparison.Ordinal);
            }
        }
        return template;
    }

    private static string Unquote(string json) =>
        json.Length >= 2 && json[0] == '"' && json[^1] == '"' ? json[1..^1] : json;

    /// <summary>Minimal JSONPath supporting "$", "$.a.b", and "$.a[0]".</summary>
    private static string? ApplyPath(JsonObject envelope, string path)
    {
        if (path == "$")
        {
            return envelope.ToJsonString();
        }

        JsonNode? node = envelope;
        foreach (var raw in path.TrimStart('$').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw;
            int bracket;
            while ((bracket = segment.IndexOf('[', StringComparison.Ordinal)) >= 0)
            {
                var name = segment[..bracket];
                if (name.Length > 0)
                {
                    node = (node as JsonObject)?[name];
                }
                var end = segment.IndexOf(']', StringComparison.Ordinal);
                if (end < 0 || !int.TryParse(segment.AsSpan(bracket + 1, end - bracket - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
                {
                    return null;
                }
                node = (node as JsonArray)?[idx];
                segment = segment[(end + 1)..];
            }
            if (segment.Length > 0)
            {
                node = (node as JsonObject)?[segment];
            }
            if (node is null)
            {
                return null;
            }
        }
        return node.ToJsonString();
    }
}
