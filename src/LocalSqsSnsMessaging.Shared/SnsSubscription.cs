using System.Text.Json.Nodes;

namespace LocalSqsSnsMessaging;

internal sealed class SnsSubscription
{
    private string _filterPolicy = string.Empty;

    public required string SubscriptionArn { get; init; }
    public required string TopicArn { get; init; }
    public required string EndPoint { get; init; }
    public required string Protocol { get; init; }
    public required bool Raw { get; set; }

    /// <summary>
    /// The subscription's filter policy JSON, or an empty string when the subscription has no
    /// filter. Assigning re-parses <see cref="ParsedFilterPolicy"/>; callers are expected to
    /// have validated the value first via <see cref="SnsFilterPolicy.Validate"/>.
    /// </summary>
    public required string FilterPolicy
    {
        get => _filterPolicy;
        set
        {
            _filterPolicy = value ?? string.Empty;
            ParsedFilterPolicy = SnsFilterPolicy.Parse(_filterPolicy);
        }
    }

    /// <summary>
    /// Whether <see cref="FilterPolicy"/> is applied to the message attributes (the default)
    /// or to the JSON message body.
    /// </summary>
    public string FilterPolicyScope { get; set; } = SnsFilterPolicy.MessageAttributesScope;

    /// <summary>The redrive policy JSON (<c>{"deadLetterTargetArn": "..."}</c>), if configured.</summary>
    public string? RedrivePolicy { get; set; }

    /// <summary>The dead-letter queue ARN extracted from <see cref="RedrivePolicy"/>.</summary>
    public string? DeadLetterTargetArn { get; set; }

    /// <summary>The subscription-level HTTP/S delivery policy JSON, if configured.</summary>
    public string? DeliveryPolicy { get; set; }

    /// <summary>
    /// True until an HTTP/S endpoint confirms the subscription via <c>ConfirmSubscription</c> (or by
    /// visiting the <c>SubscribeURL</c>). Pending subscriptions don't take part in fan-out, so a
    /// caller can't point the bus at an arbitrary URL and have it start posting there.
    /// </summary>
    public bool PendingConfirmation { get; set; }

    /// <summary>The token sent in the <c>SubscriptionConfirmation</c> message and expected back by <c>ConfirmSubscription</c>.</summary>
    public string ConfirmationToken { get; } = Guid.NewGuid().ToString("N");

    internal JsonObject? ParsedFilterPolicy { get; private set; }

    public bool IsSqs => string.Equals(Protocol, "sqs", StringComparison.OrdinalIgnoreCase);

    public bool IsHttp =>
        string.Equals(Protocol, "http", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Protocol, "https", StringComparison.OrdinalIgnoreCase);
}
