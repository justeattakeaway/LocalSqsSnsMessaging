namespace LocalSqsSnsMessaging;

internal sealed class SnsTopicResource
{
    public required string Name { get; init; }
    public required string Region { get; init; }
    public required string Arn { get; init; }
    public Dictionary<string, string> Attributes { get; } = new();

    /// <summary>
    /// Serialises the checks that weigh a topic's subscriptions against its <c>MaximumMessageSize</c>
    /// with the changes that would invalidate them, so two callers can't each see room for one more
    /// subscription, or race a raised limit against a subscription the raised limit forbids.
    /// </summary>
    internal object SubscriptionGate { get; } = new();
    internal SnsPublishAction PublishAction { get; set; } = SnsPublishAction.NullInstance;
    public Dictionary<string, string> Tags { get; } = new();
}
