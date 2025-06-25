namespace LocalSqsSnsMessaging;

internal sealed class SnsTopicResource
{
    public required string Name { get; init; }
    public required string Region { get; init; }
    public required string Arn { get; init; }
    public Dictionary<string, string> Attributes { get; } = new();
    internal SnsPublishAction PublishAction { get; set; } = SnsPublishAction.NullInstance;
    public Dictionary<string, string> Tags { get; } = new();
}
