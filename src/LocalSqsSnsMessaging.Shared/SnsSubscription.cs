namespace LocalSqsSnsMessaging;

internal sealed class SnsSubscription
{
    public required string SubscriptionArn { get; init; }
    public required string TopicArn { get; init; }
    public required string EndPoint { get; init; }
    public required string Protocol { get; init; }
    public required bool Raw { get; set; }
    public required string FilterPolicy { get; set; }
}