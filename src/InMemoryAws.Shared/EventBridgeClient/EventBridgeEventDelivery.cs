using System.Text.Json.Nodes;
using InMemoryAws.EventBridge.Model;
using SnsPublishRequest = InMemoryAws.Sns.Model.PublishRequest;
using SqsMessageAttributeValue = InMemoryAws.Sqs.Model.MessageAttributeValue;

namespace InMemoryAws;

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
/// Resource policies are evaluated as for SNS deliveries (<see cref="ResourcePolicy.EvaluateServiceDelivery"/>):
/// EventBridge delivers as <c>events.amazonaws.com</c>, with the rule as <c>aws:SourceArn</c>. A
/// policy that denies it, or that grants EventBridge deliveries without admitting this rule,
/// refuses the delivery. A queue with no policy, or a policy that says nothing about EventBridge,
/// still receives it, although AWS would refuse a queue that grants EventBridge nothing.
/// </para>
/// <para>
/// API destinations are called over HTTP in the background, retried and rate limited
/// (<c>EventBridgeEventDelivery.ApiDestinations.cs</c>).
/// </para>
/// <para>
/// Targets of other services (Lambda, Kinesis, ...), and event buses in another region or account,
/// have no in-memory backend and are skipped.
/// </para>
/// </remarks>
internal static partial class EventBridgeEventDelivery
{
    /// <summary>
    /// Routes an event put on <paramref name="eventBus"/> through its rules and delivers it to the
    /// matched rules' targets. Its trace header, if well formed, reaches SQS targets as the
    /// <c>AWSTraceHeader</c> system attribute.
    /// </summary>
    /// <param name="eventBus">The bus the event was put on.</param>
    /// <param name="event">The event.</param>
    /// <param name="cloud">The cloud that owns the targets' resources.</param>
    /// <param name="ruleFilter">Limits routing to the rules it accepts (a replay's FilterArns); null routes through every rule.</param>
    public static void Route(EventBusResource eventBus, EventBridgeEvent @event, InMemoryAwsCloud cloud, Func<RuleResource, bool>? ruleFilter = null) =>
        Route(eventBus, @event, forwarded: false, cloud, ruleFilter);

    /// <param name="forwarded">
    /// Whether the event reached <paramref name="eventBus"/> from another bus. EventBridge forwards
    /// an event between buses only once, so a forwarded event skips event bus targets.
    /// </param>
    private static void Route(EventBusResource eventBus, EventBridgeEvent @event, bool forwarded, InMemoryAwsCloud cloud, Func<RuleResource, bool>? ruleFilter = null)
    {
        foreach (var rule in eventBus.Rules.Values)
        {
            if (!rule.IsEnabled
                || (ruleFilter is not null && !ruleFilter(rule))
                || !EventBridgeEventPattern.Matches(rule.EventPattern, @event.Envelope))
            {
                continue;
            }

            DeliverToRule(rule, @event, forwarded, cloud);
        }
    }

    /// <summary>
    /// Delivers an event a rule has matched (or, for a scheduled rule, produced) to the rule's
    /// targets; an archive's managed rule stores it in the archive instead.
    /// </summary>
    public static void DeliverToRule(RuleResource rule, EventBridgeEvent @event, InMemoryAwsCloud cloud) =>
        DeliverToRule(rule, @event, forwarded: false, cloud);

    private static void DeliverToRule(RuleResource rule, EventBridgeEvent @event, bool forwarded, InMemoryAwsCloud cloud)
    {
        if (rule.Archive is { } archive)
        {
            archive.Store(@event.Envelope, cloud.TimeProvider.GetUtcNow());
            return;
        }

        // A snapshot: PutTargets and RemoveTargets replace the list rather than change it.
        foreach (var target in rule.Targets)
        {
            DeliverToTarget(new Delivery(rule, target, @event, forwarded), cloud);
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
    /// <param name="ExhaustedRetryCondition">
    /// For a failure retried until its retry policy ran out, the <c>EXHAUSTED_RETRY_CONDITION</c>
    /// attribute (<c>MaximumRetryAttempts</c> or <c>MaximumEventAgeInSeconds</c>).
    /// </param>
    /// <param name="RetryAttempts">For a retried failure, the <c>RETRY_ATTEMPTS</c> attribute: how many retries were made.</param>
    private sealed record DeliveryFailure(string ErrorCode, string ErrorMessage, string? ExhaustedRetryCondition = null, int? RetryAttempts = null);

    private static void DeliverToTarget(Delivery delivery, InMemoryAwsCloud cloud)
    {
        if (!Arn.TryParse(delivery.Target.Arn, out var arn))
        {
            return;
        }

        switch (arn.Service)
        {
            case "sqs":
            {
                var payload = EventBridgeTargetInput.Render(delivery.Target, delivery.Rule, delivery.Event);
                var failure = payload.IsValidJson ? DeliverToQueue(delivery, arn.Value, payload.Body, cloud) : InvalidJson;
                if (failure is not null)
                {
                    DeadLetter(delivery, payload.Body, failure, cloud);
                }

                break;
            }

            case "sns":
            {
                var payload = EventBridgeTargetInput.Render(delivery.Target, delivery.Rule, delivery.Event);
                var failure = payload.IsValidJson ? PublishToTopic(delivery, arn.Value, payload.Body, cloud) : InvalidJson;
                if (failure is not null)
                {
                    DeadLetter(delivery, payload.Body, failure, cloud);
                }

                break;
            }

            case "events" when arn.Resource.StartsWith("api-destination/", StringComparison.Ordinal):
            {
                var payload = EventBridgeTargetInput.Render(delivery.Target, delivery.Rule, delivery.Event);
                if (payload.IsValidJson)
                {
                    InvokeApiDestination(delivery, arn, payload.Body, cloud);
                }
                else
                {
                    DeadLetter(delivery, payload.Body, InvalidJson, cloud);
                }

                break;
            }

            case "events" when arn.Resource.StartsWith("event-bus/", StringComparison.Ordinal)
                               && string.Equals(arn.Account, cloud.CurrentAccountId, StringComparison.Ordinal):
            {
                if (ForwardToBus(delivery, arn, cloud) is { } failure)
                {
                    DeadLetter(delivery, EventBridgeJson.Serialize(delivery.Event.Envelope), failure, cloud);
                }

                break;
            }
        }
    }

    /// <summary>Sends <paramref name="body"/> to the queue <paramref name="queueArn"/> names.</summary>
    private static DeliveryFailure? DeliverToQueue(Delivery delivery, string queueArn, string body, InMemoryAwsCloud cloud)
    {
        var request = QueueDelivery(delivery, queueArn, body) with { TraceHeader = delivery.Event.TraceHeader };

        // SQS answers a sender its policy refuses as if the queue did not exist.
        return SqsServiceDelivery.Deliver(cloud, request) is SqsServiceDelivery.Outcome.Delivered or SqsServiceDelivery.Outcome.Duplicate
            ? null
            : new DeliveryFailure("NO_RESOURCE", ServiceErrorMessage(
                "The specified queue does not exist or you do not have access to it.",
                "AmazonSQS", 400, "AWS.SimpleQueueService.NonExistentQueue"));
    }

    /// <summary>
    /// What EventBridge sends to the queue <paramref name="queueArn"/> for <paramref name="delivery"/>:
    /// on a FIFO queue, in the target's message group, deduplicated by the event id.
    /// </summary>
    private static SqsServiceDelivery.Request QueueDelivery(Delivery delivery, string queueArn, string body)
    {
        var fifo = queueArn.EndsWith(".fifo", StringComparison.OrdinalIgnoreCase);
        return new SqsServiceDelivery.Request(queueArn, SqsServiceDelivery.EventsPrincipal, delivery.Rule.Arn, body)
        {
            MessageGroupId = fifo
                ? EventBridgeTargetInput.ResolveMessageGroupId(delivery.Target, delivery.Event.Envelope) ?? delivery.Target.Id ?? "eventbridge"
                : null,
            DeduplicationId = fifo ? delivery.EventId : null,
        };
    }

    /// <summary>
    /// Publishes <paramref name="body"/> to the topic <paramref name="topicArn"/> names, which fans
    /// it out to the topic's subscriptions as any <c>Publish</c> does, with no subject and no
    /// message attributes.
    /// </summary>
    private static DeliveryFailure? PublishToTopic(Delivery delivery, string topicArn, string body, InMemoryAwsCloud cloud)
    {
        if (!cloud.TryGetTopicByArn(topicArn, out var topic))
        {
            return new DeliveryFailure("NO_RESOURCE", ServiceErrorMessage(
                "Topic does not exist", "AmazonSNS", 404, "NotFound"));
        }

        string? policy;
        lock (topic.SubscriptionGate)
        {
            policy = topic.Attributes.GetValueOrDefault(SnsTopicAttributes.Policy);
        }

        var request = ResourcePolicy.ServiceRequest("Publish", topic.Arn, SqsServiceDelivery.EventsPrincipal, delivery.Rule.Arn);
        var reason = ResourcePolicy.Parse(ResourcePolicyKind.Topic, policy)?.EvaluateServiceDelivery(request) switch
        {
            ResourcePolicy.ServiceDeliveryVerdict.ExplicitlyDenied => "with an explicit deny in a resource-based policy",
            ResourcePolicy.ServiceDeliveryVerdict.NotAllowed => "because no resource-based policy allows the SNS:Publish action",
            _ => null,
        };
        if (reason is not null)
        {
            return new DeliveryFailure("NO_PERMISSIONS", ServiceErrorMessage(
                $"User: {SqsServiceDelivery.EventsPrincipal} is not authorized to perform: SNS:Publish on resource: {topic.Arn} {reason}",
                "AmazonSNS", 403, "AuthorizationError"));
        }

        // EventBridge refuses FIFO topics as targets (PutTargets), so there is no group to publish in.
        if (!topic.IsFifo)
        {
            topic.Publish(new SnsPublishRequest { TopicArn = topic.Arn, Message = body });
        }

        return null;
    }

    /// <summary>
    /// Puts the event on the bus <paramref name="busArn"/> names, where it is routed through that
    /// bus's rules. An event that was itself forwarded goes no further.
    /// Within a region the envelope is unchanged; an event forwarded to another region keeps its
    /// origin <c>region</c> but gets a new <c>id</c>, as EventBridge assigns one on arrival.
    /// </summary>
    private static DeliveryFailure? ForwardToBus(Delivery delivery, Arn busArn, InMemoryAwsCloud cloud)
    {
        if (delivery.Forwarded)
        {
            return null;
        }

        var busName = busArn.Resource["event-bus/".Length..];
        if (!cloud.TryGetEventBusByArn(busArn.Value, out var targetBus))
        {
            // The default bus always exists; until something touches it, it has no rules.
            return string.Equals(busName, "default", StringComparison.Ordinal)
                ? null
                : new DeliveryFailure("NO_RESOURCE", ServiceErrorMessage(
                    $"Event bus {busName} does not exist.", "AmazonEventBridge", 400, "ResourceNotFoundException"));
        }

        var arriving = delivery.Event;
        if (!string.Equals(busArn.Region, Arn.RegionOf(delivery.Rule.Arn) ?? cloud.CurrentRegion, StringComparison.Ordinal))
        {
            var envelope = (JsonObject)arriving.Envelope.DeepClone();
            envelope["id"] = Guid.NewGuid().ToString();
            arriving = new EventBridgeEvent { Envelope = envelope, IngestionTime = arriving.IngestionTime, TraceHeader = arriving.TraceHeader };
        }

        Route(targetBus, arriving, forwarded: true, cloud);
        return null;
    }

    /// <summary>
    /// Sends the event a target failed to receive to the target's dead-letter queue, if it has one:
    /// <paramref name="body"/> is what the target would have received, and the message attributes
    /// say why it was not. A dead-letter queue that is missing, or whose policy refuses
    /// EventBridge, loses the event.
    /// </summary>
    private static void DeadLetter(Delivery delivery, string body, DeliveryFailure failure, InMemoryAwsCloud cloud)
    {
        if (delivery.Target.DeadLetterConfig?.Arn is not { } deadLetterArn)
        {
            return;
        }

        var attributes = new Dictionary<string, SqsMessageAttributeValue>(StringComparer.Ordinal)
        {
            ["ERROR_CODE"] = StringAttribute(failure.ErrorCode),
            ["ERROR_MESSAGE"] = StringAttribute(failure.ErrorMessage),
            ["RULE_ARN"] = StringAttribute(delivery.Rule.Arn),
            ["TARGET_ARN"] = StringAttribute(delivery.Target.Arn ?? string.Empty),
        };
        if (failure.ExhaustedRetryCondition is { } condition)
        {
            attributes["EXHAUSTED_RETRY_CONDITION"] = StringAttribute(condition);
        }

        if (failure.RetryAttempts is { } retries)
        {
            attributes["RETRY_ATTEMPTS"] = StringAttribute(retries.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        SqsServiceDelivery.Deliver(cloud, QueueDelivery(delivery, deadLetterArn, body) with { MessageAttributes = attributes });
    }

    /// <summary>A target service's error as the AWS SDK for Java words it, which is how EventBridge records it.</summary>
    private static string ServiceErrorMessage(string message, string service, int statusCode, string errorCode) =>
        $"{message} (Service: {service}; Status Code: {statusCode}; Error Code: {errorCode}; Request ID: {Guid.NewGuid()}; Proxy: null)";

    private static SqsMessageAttributeValue StringAttribute(string value) =>
        new() { DataType = "String", StringValue = value };
}
