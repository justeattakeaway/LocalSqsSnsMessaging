#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable reference warnings - internal POCOs use nullable properties but values are set at runtime

using System.Text.Json;
using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.Sns.Model;

namespace LocalSqsSnsMessaging;

/// <summary>Subscription operations.</summary>
internal sealed partial class InternalSnsClient
{
    /// <summary>SNS answers every request for a subscription that doesn't exist with this message.</summary>
    internal const string SubscriptionDoesNotExist = "Subscription does not exist";

    /// <summary>
    /// The protocols SNS knows. This bus delivers to <c>sqs</c>, <c>http</c> and <c>https</c>, and
    /// takes <c>email</c> and <c>email-json</c> subscriptions, which stay pending confirmation.
    /// </summary>
    private static readonly string[] KnownProtocols = ["http", "https", "email", "email-json", "sms", "sqs", "application", "lambda", "firehose"];

    /// <summary>The subscription attributes a caller can set, at <c>Subscribe</c> or afterwards.</summary>
    private static readonly HashSet<string> SettableSubscriptionAttributes = new(StringComparer.Ordinal)
    {
        SnsSubscriptionAttributes.RawMessageDelivery,
        SnsSubscriptionAttributes.FilterPolicy,
        SnsSubscriptionAttributes.FilterPolicyScope,
        SnsSubscriptionAttributes.RedrivePolicy,
        SnsSubscriptionAttributes.DeliveryPolicy,
        SnsSubscriptionAttributes.SubscriptionRoleArn,
        SnsSubscriptionAttributes.ReplayPolicy,
    };

    public Task<SubscribeResponse> SubscribeAsync(SubscribeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topic = GetTopicByArn(request.TopicArn);
        var protocol = NormalizeProtocol(request.Protocol);
        Authorize(topic, SnsActionName.Subscribe, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["sns:Protocol"] = protocol,
            ["sns:Endpoint"] = request.Endpoint,
        });
        switch (protocol)
        {
            case "sqs":
                if (!SnsArn.IsSqsQueueArn(request.Endpoint))
                {
                    throw new InternalInvalidParameterException("Invalid parameter: SQS endpoint ARN");
                }

                if (request.Endpoint.EndsWith(".fifo", StringComparison.Ordinal) && !topic.IsFifo)
                {
                    throw new InternalInvalidParameterException(
                        "Invalid parameter: Invalid parameter: Endpoint Reason: FIFO SQS Queues can not be subscribed to standard SNS topics");
                }

                break;

            case "http":
            case "https":
                if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpointUri) ||
                    !string.Equals(endpointUri.Scheme, protocol, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InternalInvalidParameterException(
                        $"Invalid parameter: Endpoint: Endpoint must be a valid {protocol} URL");
                }

                break;

            case "email":
            case "email-json":
                // SNS takes no email subscription on a FIFO topic.
                if (topic.IsFifo)
                {
                    throw new InternalInvalidParameterException($"Invalid parameter: Invalid protocol type: {protocol}");
                }

                if (!IsEmailAddress(request.Endpoint))
                {
                    throw new InternalInvalidParameterException("Invalid parameter: Email address");
                }

                break;

            default:
                if (!KnownProtocols.Contains(protocol, StringComparer.Ordinal))
                {
                    throw new InternalInvalidParameterException(
                        $"Invalid parameter: Amazon SNS does not support this protocol string: {request.Protocol}");
                }

                throw new NotSupportedException("Only the sqs, http, https, email and email-json protocols are supported.");
        }

        var requested = request.Attributes ?? [];
        foreach (var name in requested.Keys)
        {
            if (!SettableSubscriptionAttributes.Contains(name))
            {
                throw new InternalInvalidParameterException($"Invalid parameter: Attributes Reason: Unknown attribute {name}");
            }
        }

        var snsSubscription = new SnsSubscription
        {
            SubscriptionArn = SnsArn.NewSubscriptionArn(topic.Arn),
            Topic = topic,
            EndPoint = request.Endpoint,
            Protocol = protocol,
            Raw = false,
            FilterPolicy = string.Empty,
            // HTTP/S and email endpoints have to confirm before they receive anything, as on AWS.
            // The bus sends no email, so an email subscription stays pending until ConfirmSubscription
            // presents its token.
            PendingConfirmation = protocol is "http" or "https" or "email" or "email-json"
        };

        // Apply the scope before the policy so a nested (body-scoped) policy validates correctly.
        if (requested.TryGetValue(SnsSubscriptionAttributes.FilterPolicyScope, out var scope))
        {
            ApplySubscriptionAttribute(topic, snsSubscription, SnsSubscriptionAttributes.FilterPolicyScope, scope, atSubscribe: true);
        }

        foreach (var (name, value) in requested)
        {
            if (!name.Equals(SnsSubscriptionAttributes.FilterPolicyScope, StringComparison.Ordinal))
            {
                ApplySubscriptionAttribute(topic, snsSubscription, name, value, atSubscribe: true);
            }
        }

        // Subscribing the same endpoint again returns the existing subscription, unless the request
        // asks for attributes it doesn't have.
        var subscription = topic.Subscribe(snsSubscription, existing =>
        {
            if (requested.Any(a => !SubscriptionAttributeMatches(existing, a.Key, a.Value)))
            {
                throw new InternalInvalidParameterException(
                    "Invalid parameter: Attributes Reason: Subscription already exists with different attributes");
            }
        });

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.Subscribe, request.TopicArn);
        return Task.FromResult(new SubscribeResponse
        {
            // AWS only hands back the ARN of an unconfirmed subscription when asked to.
            SubscriptionArn = subscription.PendingConfirmation && request.ReturnSubscriptionArn != true
                ? "pending confirmation"
                : subscription.SubscriptionArn
        }.SetCommonProperties());
    }

    public Task<UnsubscribeResponse> UnsubscribeAsync(UnsubscribeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        SnsArn.Parse(request.SubscriptionArn, "SubscriptionArn");

        // Unsubscribing a subscription that doesn't exist succeeds, as on AWS. One still pending
        // confirmation can't be unsubscribed (DeleteTopic removes it).
        if (_resources.Subscriptions.TryGetValue(request.SubscriptionArn, out var subscription))
        {
            if (subscription.PendingConfirmation)
            {
                throw new InternalInvalidParameterException(
                    "Invalid parameter: SubscriptionArn Reason: Cannot unsubscribe a subscription that is pending confirmation");
            }

            subscription.Topic.Unsubscribe(subscription);
        }

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.Unsubscribe, request.SubscriptionArn);
        return Task.FromResult(new UnsubscribeResponse().SetCommonProperties());
    }

    public Task<GetSubscriptionAttributesResponse> GetSubscriptionAttributesAsync(
        GetSubscriptionAttributesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var subscription = GetSubscriptionByArn(request.SubscriptionArn);

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SubscriptionArn"] = subscription.SubscriptionArn,
            ["TopicArn"] = subscription.TopicArn,
            ["Protocol"] = subscription.Protocol,
            ["Endpoint"] = subscription.EndPoint,
            ["Owner"] = _bus.CurrentAccountId,
            ["SubscriptionPrincipal"] = $"arn:aws:iam::{_bus.CurrentAccountId}:root",
            // SNS confirms an SQS subscription itself, as the subscribing principal.
            ["ConfirmationWasAuthenticated"] = subscription.IsSqs ? "true" : "false",
            ["PendingConfirmation"] = subscription.PendingConfirmation ? "true" : "false",
            [SnsSubscriptionAttributes.RawMessageDelivery] = subscription.Raw ? "true" : "false",
        };

        // A filter policy scope is only reported alongside a policy it applies to.
        if (!string.IsNullOrEmpty(subscription.FilterPolicy))
        {
            attributes[SnsSubscriptionAttributes.FilterPolicy] = subscription.FilterPolicy;
            attributes[SnsSubscriptionAttributes.FilterPolicyScope] = subscription.FilterPolicyScope;
        }
        if (subscription.RedrivePolicy is not null)
        {
            attributes[SnsSubscriptionAttributes.RedrivePolicy] = subscription.RedrivePolicy;
        }
        if (subscription.DeliveryPolicy is not null)
        {
            attributes[SnsSubscriptionAttributes.DeliveryPolicy] = subscription.DeliveryPolicy;
        }
        if (subscription.SubscriptionRoleArn is not null)
        {
            attributes[SnsSubscriptionAttributes.SubscriptionRoleArn] = subscription.SubscriptionRoleArn;
        }
        if (subscription.ReplayPolicy is not null)
        {
            attributes[SnsSubscriptionAttributes.ReplayPolicy] = subscription.ReplayPolicy;
            attributes[SnsSubscriptionAttributes.ReplayStatus] = subscription.ReplayStatus;
        }

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.GetSubscriptionAttributes, subscription.SubscriptionArn);
        return Task.FromResult(new GetSubscriptionAttributesResponse
        {
            Attributes = attributes
        }.SetCommonProperties());
    }

    public Task<SetSubscriptionAttributesResponse> SetSubscriptionAttributesAsync(
        SetSubscriptionAttributesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var subscription = GetSubscriptionByArn(request.SubscriptionArn);
        if (request.AttributeName is null || !SettableSubscriptionAttributes.Contains(request.AttributeName))
        {
            throw new InternalInvalidParameterException("Invalid parameter: AttributeName");
        }

        // A replay policy set on an existing subscription starts a replay straight away.
        subscription.Topic.UpdateSubscription(subscription, s =>
            ApplySubscriptionAttribute(s.Topic, s, request.AttributeName, request.AttributeValue, atSubscribe: false));

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.SetSubscriptionAttributes, subscription.SubscriptionArn);
        return Task.FromResult(new SetSubscriptionAttributesResponse().SetCommonProperties());
    }

    public Task<ListSubscriptionsResponse> ListSubscriptionsAsync(ListSubscriptionsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (items, nextToken) = ListSubscriptionPage(_resources.Subscriptions.Values, request.NextToken);

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.ListSubscriptions);
        return Task.FromResult(new ListSubscriptionsResponse
        {
            Subscriptions = items,
            NextToken = nextToken
        }.SetCommonProperties());
    }

    public Task<ListSubscriptionsByTopicResponse> ListSubscriptionsByTopicAsync(ListSubscriptionsByTopicRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var topic = GetTopicByArn(request.TopicArn);
        Authorize(topic, SnsActionName.ListSubscriptionsByTopic);
        var (items, nextToken) = ListSubscriptionPage(
            topic.Subscriptions,
            request.NextToken);

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.ListSubscriptionsByTopic, request.TopicArn);
        return Task.FromResult(new ListSubscriptionsByTopicResponse
        {
            Subscriptions = items,
            NextToken = nextToken
        }.SetCommonProperties());
    }

    public Task<ConfirmSubscriptionResponse> ConfirmSubscriptionAsync(ConfirmSubscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A token is only good for the topic it was issued for: any other TopicArn, well formed or
        // not, refuses the token rather than the ARN.
        var subscription = _bus.TryGetTopicByArn(request.TopicArn, out var topic)
                           && string.Equals(topic.Region, _resources.Region, StringComparison.Ordinal)
            ? topic.Confirm(request.Token)
            : null;
        if (subscription is null)
        {
            throw new InternalInvalidParameterException("Invalid parameter: Token");
        }

        _bus.RecordOperation(AwsServiceName.Sns, SnsActionName.ConfirmSubscription, subscription.SubscriptionArn);
        return Task.FromResult(new ConfirmSubscriptionResponse
        {
            SubscriptionArn = subscription.SubscriptionArn
        }.SetCommonProperties());
    }

    /// <summary>One page of <paramref name="subscriptions"/> as SNS lists them, in a stable order.</summary>
    private (List<Subscription> Items, string? NextToken) ListSubscriptionPage(IEnumerable<SnsSubscription> subscriptions, string? nextToken)
    {
        var byArn = subscriptions.ToDictionary(s => s.SubscriptionArn, StringComparer.Ordinal);
        var (arns, token) = SnsPagination.Page(byArn.Keys, nextToken);
        var items = arns.Select(arn => byArn[arn]).Select(s => new Subscription
        {
            SubscriptionArn = s.PendingConfirmation ? "PendingConfirmation" : s.SubscriptionArn,
            Owner = _bus.CurrentAccountId,
            Protocol = s.Protocol,
            Endpoint = s.EndPoint,
            TopicArn = s.TopicArn,
        }).ToList();
        return (items, token);
    }

    /// <summary>Whether <paramref name="endpoint"/> looks like an email address: one <c>@</c> with something either side.</summary>
    private static bool IsEmailAddress(string? endpoint)
    {
        var at = endpoint?.IndexOf('@', StringComparison.Ordinal) ?? -1;
        return at > 0 && at < endpoint!.Length - 1 && endpoint.IndexOf('@', at + 1) < 0
               && !endpoint.Any(char.IsWhiteSpace);
    }

    /// <summary>The subscription <paramref name="subscriptionArn"/> names, or SNS's <c>NotFound</c>.</summary>
    private SnsSubscription GetSubscriptionByArn(string? subscriptionArn)
    {
        SnsArn.Parse(subscriptionArn, "SubscriptionArn");
        return _resources.Subscriptions.TryGetValue(subscriptionArn, out var subscription)
            ? subscription
            : throw new InternalNotFoundException(SubscriptionDoesNotExist);
    }

    /// <summary>
    /// Whether <paramref name="subscription"/> already has <paramref name="name"/> set to
    /// <paramref name="value"/>, comparing JSON policies as documents rather than as text.
    /// </summary>
    private static bool SubscriptionAttributeMatches(SnsSubscription subscription, string name, string? value)
    {
        value ??= string.Empty;
        return name switch
        {
            SnsSubscriptionAttributes.RawMessageDelivery => bool.TryParse(value, out var raw) && raw == subscription.Raw,
            SnsSubscriptionAttributes.FilterPolicy => JsonEquivalent(subscription.FilterPolicy, value),
            SnsSubscriptionAttributes.FilterPolicyScope => string.Equals(subscription.FilterPolicyScope, value, StringComparison.Ordinal),
            SnsSubscriptionAttributes.RedrivePolicy => JsonEquivalent(subscription.RedrivePolicy ?? string.Empty, value),
            SnsSubscriptionAttributes.DeliveryPolicy => JsonEquivalent(subscription.DeliveryPolicy ?? string.Empty,
                string.IsNullOrWhiteSpace(value) ? value : SnsDeliveryPolicy.NormalizeSubscriptionPolicy(value)),
            SnsSubscriptionAttributes.SubscriptionRoleArn => string.Equals(subscription.SubscriptionRoleArn ?? string.Empty, value, StringComparison.Ordinal),
            SnsSubscriptionAttributes.ReplayPolicy => JsonEquivalent(subscription.ReplayPolicy ?? string.Empty, value),
            _ => false,
        };
    }

    private static bool JsonEquivalent(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b);
        }

        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(a), JsonNode.Parse(b));
        }
        catch (JsonException)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Validates and applies a single subscription attribute. <paramref name="atSubscribe"/> is true
    /// when it came in <c>Subscribe</c>'s attribute map, where SNS reports a bad value as a reason
    /// against <c>Attributes</c>. <paramref name="topic"/> is the subscription's topic, which some
    /// attributes are validated against.
    /// </summary>
    private static void ApplySubscriptionAttribute(SnsTopicResource topic, SnsSubscription subscription, string name, string? value, bool atSubscribe)
    {
        value ??= string.Empty;
        try
        {
            switch (name)
            {
                case SnsSubscriptionAttributes.RawMessageDelivery:
                    if (!bool.TryParse(value, out var isRawMessageDelivery))
                    {
                        throw new InternalInvalidParameterException(
                            $"Invalid parameter: RawMessageDelivery: Invalid value [{value}]. Must be true or false.");
                    }

                    subscription.Raw = isRawMessageDelivery;
                    break;

                case SnsSubscriptionAttributes.FilterPolicy:
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        SnsFilterPolicy.Validate(value, subscription.FilterPolicyScope);
                    }

                    subscription.FilterPolicy = string.IsNullOrWhiteSpace(value) ? string.Empty : value;
                    break;

                case SnsSubscriptionAttributes.FilterPolicyScope:
                    if (!SnsFilterPolicy.IsValidScope(value))
                    {
                        throw new InternalInvalidParameterException(
                            $"Invalid parameter: FilterPolicyScope: Invalid value [{value}]. Please use either MessageBody or MessageAttributes");
                    }

                    SnsFilterPolicy.Validate(subscription.FilterPolicy, value);
                    subscription.FilterPolicyScope = value;
                    break;

                case SnsSubscriptionAttributes.RedrivePolicy:
                    var deadLetterTargetArn = SnsRedrivePolicy.ParseDeadLetterTargetArn(value);
                    if (deadLetterTargetArn is not null && topic.IsFifo && subscription.IsSqs
                        && subscription.EndPoint.EndsWith(".fifo", StringComparison.Ordinal)
                        && !deadLetterTargetArn.EndsWith(".fifo", StringComparison.Ordinal))
                    {
                        throw new InternalInvalidParameterException(
                            "Invalid parameter: RedrivePolicy: must use a FIFO queue as DLQ for a FIFO Subscription to a FIFO Topic.");
                    }

                    subscription.DeadLetterTargetArn = deadLetterTargetArn;
                    subscription.RedrivePolicy = deadLetterTargetArn is null ? null : value;
                    break;

                case SnsSubscriptionAttributes.DeliveryPolicy:
                    SnsDeliveryPolicy.Validate(value);
                    subscription.DeliveryPolicy = string.IsNullOrWhiteSpace(value) ? null : SnsDeliveryPolicy.NormalizeSubscriptionPolicy(value);
                    break;

                case SnsSubscriptionAttributes.SubscriptionRoleArn:
                    subscription.SubscriptionRoleArn = string.IsNullOrEmpty(value) ? null : value;
                    break;

                case SnsSubscriptionAttributes.ReplayPolicy:
                    if (string.IsNullOrEmpty(value))
                    {
                        subscription.ReplayPolicy = null;
                        subscription.ReplayStatus = null;
                        break;
                    }

                    SnsReplayPolicy.Validate(value, topic);
                    subscription.ReplayPolicy = value;
                    subscription.ReplayStatus = SnsReplayPolicy.Pending;
                    break;

                default:
                    throw new InternalInvalidParameterException("Invalid parameter: AttributeName");
            }
        }
        catch (InternalInvalidParameterException ex) when (atSubscribe
            && ex.Message.StartsWith("Invalid parameter: ", StringComparison.Ordinal)
            && !ex.Message.StartsWith("Invalid parameter: Attributes Reason: ", StringComparison.Ordinal)
            && name is SnsSubscriptionAttributes.FilterPolicy or SnsSubscriptionAttributes.ReplayPolicy)
        {
            throw new InternalInvalidParameterException(
                "Invalid parameter: Attributes Reason: " + ex.Message["Invalid parameter: ".Length..]);
        }
    }

    private static string? NormalizeProtocol(string? protocol)
    {
        foreach (var known in KnownProtocols)
        {
            if (string.Equals(protocol, known, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }
        return protocol;
    }
}

/// <summary>The subscription attribute names SNS defines.</summary>
internal static class SnsSubscriptionAttributes
{
    public const string RawMessageDelivery = "RawMessageDelivery";
    public const string FilterPolicy = "FilterPolicy";
    public const string FilterPolicyScope = "FilterPolicyScope";
    public const string RedrivePolicy = "RedrivePolicy";
    public const string DeliveryPolicy = "DeliveryPolicy";
    public const string SubscriptionRoleArn = "SubscriptionRoleArn";
    public const string ReplayPolicy = "ReplayPolicy";
    public const string ReplayStatus = "ReplayStatus";
}
