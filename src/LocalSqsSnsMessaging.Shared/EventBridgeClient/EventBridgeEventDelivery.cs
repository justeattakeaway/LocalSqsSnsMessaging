using System.Security.Cryptography;
using System.Text;
using LocalSqsSnsMessaging.EventBridge.Model;
using SnsPublishRequest = LocalSqsSnsMessaging.Sns.Model.PublishRequest;
using SqsMessage = LocalSqsSnsMessaging.Sqs.Model.Message;
using SqsMessageAttributeValue = LocalSqsSnsMessaging.Sqs.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Routes event envelopes through a bus's rules and delivers each matched event to the rule's
/// targets that have an in-memory backend: SQS queues, SNS topics and event buses.
/// <see cref="EventBridgeEnvelope"/> builds the envelope and <see cref="EventBridgeTargetInput"/>
/// renders what each target receives.
/// </summary>
/// <remarks>
/// <para>
/// Every enabled rule whose pattern matches delivers its own copy to each of its targets, during
/// <c>PutEvents</c>. A target whose resource is missing, whose resource policy refuses
/// EventBridge, or whose rendered input is not JSON, fails: the event goes to the target's <c>DeadLetterConfig</c> queue with the
/// <c>ERROR_CODE</c>, <c>ERROR_MESSAGE</c>, <c>RULE_ARN</c> and <c>TARGET_ARN</c> attributes
/// EventBridge writes, or is dropped when the target has none. EventBridge does not retry these
/// failures, so a target's <c>RetryPolicy</c> never delays them; it retries only throttling and
/// service errors, which the in-memory services never return.
/// </para>
/// <para>
/// Resource policies are evaluated as for SNS deliveries (<see cref="SqsQueuePolicy.AdmitsServiceDelivery"/>):
/// EventBridge delivers as <c>events.amazonaws.com</c>, with the rule as <c>aws:SourceArn</c>. A
/// policy that denies it, or that grants EventBridge deliveries without admitting this rule,
/// refuses the delivery. A queue with no policy, or a policy that says nothing about EventBridge,
/// still receives it, although AWS would refuse a queue that grants EventBridge nothing.
/// </para>
/// <para>
/// Targets of other services (Lambda, Kinesis, ...), and event buses in another region or account,
/// have no in-memory backend and are skipped.
/// </para>
/// </remarks>
internal static class EventBridgeEventDelivery
{
    /// <summary>The service principal EventBridge delivers to targets as.</summary>
    private const string EventsServicePrincipal = "events.amazonaws.com";

    /// <summary>
    /// Routes an event put on <paramref name="eventBus"/> through its rules and delivers it to the
    /// matched rules' targets. Its trace header, if well formed, reaches SQS targets as the
    /// <c>AWSTraceHeader</c> system attribute.
    /// </summary>
    /// <param name="eventBus">The bus the event was put on.</param>
    /// <param name="event">The event.</param>
    /// <param name="bus">The bus that owns the targets' resources.</param>
    public static void Route(EventBusResource eventBus, EventBridgeEvent @event, InMemoryAwsBus bus) =>
        Route(eventBus, @event, forwarded: false, bus);

    /// <param name="forwarded">
    /// Whether the event reached <paramref name="eventBus"/> from another bus. EventBridge forwards
    /// an event between buses only once, so a forwarded event skips event bus targets.
    /// </param>
    private static void Route(EventBusResource eventBus, EventBridgeEvent @event, bool forwarded, InMemoryAwsBus bus)
    {
        foreach (var rule in eventBus.Rules.Values)
        {
            if (!rule.IsEnabled || !EventBridgeEventPattern.Matches(rule.EventPattern, @event.Envelope))
            {
                continue;
            }

            // A snapshot: PutTargets and RemoveTargets replace the list rather than change it.
            foreach (var target in rule.Targets)
            {
                DeliverToTarget(new Delivery(rule, target, @event, forwarded), bus);
            }
        }
    }

    /// <summary>One matched event on its way to one of the matching rule's targets.</summary>
    private sealed record Delivery(RuleResource Rule, Target Target, EventBridgeEvent Event, bool Forwarded)
    {
        public string EventId => Event.Envelope["id"]?.GetValue<string>() ?? string.Empty;
    }

    /// <summary>EventBridge's failure for an input transformer that renders something that is not JSON.</summary>
    private static readonly DeliveryFailure InvalidJson = new("INVALID_JSON", "Invalid input for target.");

    /// <summary>Why EventBridge could not deliver to a target, as it records it on the dead letter.</summary>
    /// <param name="ErrorCode">The <c>ERROR_CODE</c> attribute, e.g. <c>NO_RESOURCE</c>.</param>
    /// <param name="ErrorMessage">The <c>ERROR_MESSAGE</c> attribute: the target service's error, as its SDK words it.</param>
    private sealed record DeliveryFailure(string ErrorCode, string ErrorMessage);

    private static void DeliverToTarget(Delivery delivery, InMemoryAwsBus bus)
    {
        if (!TargetArn.TryParse(delivery.Target.Arn, out var arn))
        {
            return;
        }

        switch (arn.Service)
        {
            case "sqs":
            {
                var payload = EventBridgeTargetInput.Render(delivery.Target, delivery.Rule, delivery.Event);
                var failure = payload.IsValidJson ? DeliverToQueue(delivery, arn.Value, payload.Body, bus) : InvalidJson;
                if (failure is not null)
                {
                    DeadLetter(delivery, payload.Body, failure, bus);
                }

                break;
            }

            case "sns":
            {
                var payload = EventBridgeTargetInput.Render(delivery.Target, delivery.Rule, delivery.Event);
                var failure = payload.IsValidJson ? PublishToTopic(delivery, arn.Value, payload.Body, bus) : InvalidJson;
                if (failure is not null)
                {
                    DeadLetter(delivery, payload.Body, failure, bus);
                }

                break;
            }

            case "events" when arn.Resource.StartsWith("event-bus/", StringComparison.Ordinal)
                               && string.Equals(arn.Region, bus.CurrentRegion, StringComparison.Ordinal)
                               && string.Equals(arn.Account, bus.CurrentAccountId, StringComparison.Ordinal):
            {
                if (ForwardToBus(delivery, arn.Resource["event-bus/".Length..], bus) is { } failure)
                {
                    DeadLetter(delivery, EventBridgeJson.Serialize(delivery.Event.Envelope), failure, bus);
                }

                break;
            }
        }
    }

    /// <summary>Sends <paramref name="body"/> to the queue <paramref name="queueArn"/> names.</summary>
    private static DeliveryFailure? DeliverToQueue(Delivery delivery, string queueArn, string body, InMemoryAwsBus bus)
    {
        // SQS answers a sender its policy refuses as if the queue did not exist.
        if (!bus.TryGetQueueByArn(queueArn, out var queue) || !AdmitsDelivery(queue, delivery.Rule))
        {
            return new DeliveryFailure("NO_RESOURCE", ServiceErrorMessage(
                "The specified queue does not exist or you do not have access to it.",
                "AmazonSQS", 400, "AWS.SimpleQueueService.NonExistentQueue"));
        }

        var message = CreateSqsMessage(body, attributes: null);
        if (delivery.Event.TraceHeader is not null)
        {
            message.Attributes = new() { [InternalMessageSystemAttributeName.AWSTraceHeader] = delivery.Event.TraceHeader };
        }

        Enqueue(queue, message, delivery, bus);
        return null;
    }

    /// <summary>
    /// Publishes <paramref name="body"/> to the topic <paramref name="topicArn"/> names, which fans
    /// it out to the topic's subscriptions as any <c>Publish</c> does, with no subject and no
    /// message attributes.
    /// </summary>
    private static DeliveryFailure? PublishToTopic(Delivery delivery, string topicArn, string body, InMemoryAwsBus bus)
    {
        if (!bus.Topics.TryGetValue(topicArn[(topicArn.LastIndexOf(':') + 1)..], out var topic)
            || !string.Equals(topic.Arn, topicArn, StringComparison.Ordinal))
        {
            return new DeliveryFailure("NO_RESOURCE", ServiceErrorMessage(
                "Topic does not exist", "AmazonSNS", 404, "NotFound"));
        }

        string? policy;
        lock (topic.SubscriptionGate)
        {
            policy = topic.Attributes.GetValueOrDefault(SnsTopicAttributes.Policy);
        }

        var request = ServiceRequest("Publish", topic.Arn, delivery.Rule) with { ActionPrefix = "sns" };
        if (!string.IsNullOrEmpty(policy) && !SqsQueuePolicy.AdmitsServiceDelivery(policy!, request))
        {
            var reason = SqsQueuePolicy.DeniesExplicitly(policy!, request)
                ? "with an explicit deny in a resource-based policy"
                : "because no resource-based policy allows the SNS:Publish action";
            return new DeliveryFailure("NO_PERMISSIONS", ServiceErrorMessage(
                $"User: {EventsServicePrincipal} is not authorized to perform: SNS:Publish on resource: {topic.Arn} {reason}",
                "AmazonSNS", 403, "AuthorizationError"));
        }

        // EventBridge refuses FIFO topics as targets (PutTargets), so there is no group to publish in.
        if (!topic.IsFifo)
        {
            topic.PublishAction.Execute(new SnsPublishRequest { TopicArn = topic.Arn, Message = body });
        }

        return null;
    }

    /// <summary>
    /// Puts the event on the bus named <paramref name="busName"/>, envelope unchanged, where it is
    /// routed through that bus's rules. An event that was itself forwarded goes no further.
    /// </summary>
    private static DeliveryFailure? ForwardToBus(Delivery delivery, string busName, InMemoryAwsBus bus)
    {
        if (delivery.Forwarded)
        {
            return null;
        }

        if (!bus.EventBuses.TryGetValue(busName, out var targetBus))
        {
            // The default bus always exists; until something touches it, it has no rules.
            return string.Equals(busName, "default", StringComparison.Ordinal)
                ? null
                : new DeliveryFailure("NO_RESOURCE", ServiceErrorMessage(
                    $"Event bus {busName} does not exist.", "AmazonEventBridge", 400, "ResourceNotFoundException"));
        }

        Route(targetBus, delivery.Event, forwarded: true, bus);
        return null;
    }

    /// <summary>
    /// Sends the event a target failed to receive to the target's dead-letter queue, if it has one:
    /// <paramref name="body"/> is what the target would have received, and the message attributes
    /// say why it was not. A dead-letter queue that is missing, or whose policy refuses
    /// EventBridge, loses the event.
    /// </summary>
    private static void DeadLetter(Delivery delivery, string body, DeliveryFailure failure, InMemoryAwsBus bus)
    {
        if (delivery.Target.DeadLetterConfig?.Arn is not { } deadLetterArn
            || !bus.TryGetQueueByArn(deadLetterArn, out var deadLetterQueue)
            || !AdmitsDelivery(deadLetterQueue, delivery.Rule))
        {
            return;
        }

        var message = CreateSqsMessage(body, new Dictionary<string, SqsMessageAttributeValue>(StringComparer.Ordinal)
        {
            ["ERROR_CODE"] = StringAttribute(failure.ErrorCode),
            ["ERROR_MESSAGE"] = StringAttribute(failure.ErrorMessage),
            ["RULE_ARN"] = StringAttribute(delivery.Rule.Arn),
            ["TARGET_ARN"] = StringAttribute(delivery.Target.Arn ?? string.Empty),
        });
        Enqueue(deadLetterQueue, message, delivery, bus);
    }

    /// <summary>
    /// Enqueues <paramref name="message"/> as a <c>SendMessage</c> would, after the queue's
    /// <c>DelaySeconds</c>. On a FIFO queue it joins the target's message group, deduplicated by
    /// the event id.
    /// </summary>
    private static void Enqueue(SqsQueueResource queue, SqsMessage message, Delivery delivery, InMemoryAwsBus bus)
    {
        if (!queue.IsFifo)
        {
            if (queue.DelaySeconds > 0)
            {
                queue.EnqueueAfterDelay(message, TimeSpan.FromSeconds(queue.DelaySeconds), bus.TimeProvider);
            }
            else
            {
                queue.Messages.Enqueue(message);
            }

            return;
        }

        var groupId = EventBridgeTargetInput.ResolveMessageGroupId(delivery.Target, delivery.Event.Envelope) ?? delivery.Target.Id ?? "eventbridge";
        message.Attributes ??= [];
        message.Attributes[InternalMessageSystemAttributeName.MessageGroupId] = groupId;
        message.Attributes[InternalMessageSystemAttributeName.MessageDeduplicationId] = delivery.EventId;
        queue.TryEnqueueFifoSend(message, groupId, delivery.EventId, DeduplicatesPerMessageGroup(queue), bus.TimeProvider, out _);
    }

    /// <summary>Whether the FIFO queue is a high-throughput one that deduplicates within each message group.</summary>
    private static bool DeduplicatesPerMessageGroup(SqsQueueResource queue) =>
        queue.Attributes is { } attributes
        && attributes.TryGetValue(InternalQueueAttributeName.DeduplicationScope, out var scope)
        && string.Equals(scope, "messageGroup", StringComparison.Ordinal)
        && attributes.TryGetValue(InternalQueueAttributeName.FifoThroughputLimit, out var limit)
        && string.Equals(limit, "perMessageGroupId", StringComparison.Ordinal);

    /// <summary>Whether <paramref name="queue"/>'s policy lets <paramref name="rule"/> send to it.</summary>
    private static bool AdmitsDelivery(SqsQueueResource queue, RuleResource rule) =>
        queue.Attributes?.TryGetValue(InternalQueueAttributeName.Policy, out var policy) != true
        || string.IsNullOrEmpty(policy)
        || SqsQueuePolicy.AdmitsServiceDelivery(policy!, ServiceRequest(SqsActionName.SendMessage, queue.Arn, rule));

    /// <summary>A request by EventBridge, on <paramref name="rule"/>'s behalf, to act on a target resource.</summary>
    private static SqsQueuePolicy.RequestContext ServiceRequest(string action, string resource, RuleResource rule) =>
        new(action, resource, PrincipalAccount: null, PrincipalArn: null, SourceIp: null, SecureTransport: true)
        {
            Service = EventsServicePrincipal,
            SourceArn = rule.Arn,
            SourceAccount = rule.Arn.Split(':')[4],
        };

    /// <summary>A target service's error as the AWS SDK for Java words it, which is how EventBridge records it.</summary>
    private static string ServiceErrorMessage(string message, string service, int statusCode, string errorCode) =>
        $"{message} (Service: {service}; Status Code: {statusCode}; Error Code: {errorCode}; Request ID: {Guid.NewGuid()}; Proxy: null)";

    private static SqsMessageAttributeValue StringAttribute(string value) =>
        new() { DataType = "String", StringValue = value };

    private static SqsMessage CreateSqsMessage(string body, Dictionary<string, SqsMessageAttributeValue>? attributes)
    {
#pragma warning disable CA5351, CA1308 // SQS's MD5 digest is lowercase hex
        return new SqsMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            Body = body,
            MD5OfBody = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant(),
            MessageAttributes = attributes,
        };
#pragma warning restore CA5351, CA1308
    }

    /// <summary>The parts of a target ARN: <c>arn:partition:service:region:account:resource</c>.</summary>
    private readonly record struct TargetArn(string Value, string Service, string Region, string Account, string Resource)
    {
        public static bool TryParse(string? arn, out TargetArn parsed)
        {
            var parts = arn?.Split([':'], 6);
            if (parts is not { Length: 6 } || !string.Equals(parts[0], "arn", StringComparison.Ordinal))
            {
                parsed = default;
                return false;
            }

            parsed = new TargetArn(arn!, parts[2], parts[3], parts[4], parts[5]);
            return true;
        }
    }
}
