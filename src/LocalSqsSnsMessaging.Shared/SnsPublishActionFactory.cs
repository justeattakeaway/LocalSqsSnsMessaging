namespace LocalSqsSnsMessaging;

public static class SnsPublishActionFactory
{
    public static void UpdateTopicPublishAction(string topicArn, InMemoryAwsBus bus)
    {
        ArgumentNullException.ThrowIfNull(topicArn);
        ArgumentNullException.ThrowIfNull(bus);

        var topicName = GetNameFromArn(topicArn);
        if (!bus.Topics.TryGetValue(topicName, out var topic))
        {
            throw new InvalidOperationException($"Topic not found: {topicArn}");
        }

        // Endpoints are resolved at publish time rather than captured here, so a queue deleted
        // after subscribing is treated as a delivery failure (and dead-lettered if configured)
        // instead of silently receiving messages through a stale reference.
        var subscriptions = bus.Subscriptions.Values
            .Where(s => s.TopicArn == topicArn)
            .ToList();

        topic.PublishAction = new SnsPublishAction(subscriptions, topic, bus);
    }

    private static string GetNameFromArn(string arn) => arn.Split(':').Last();
}
