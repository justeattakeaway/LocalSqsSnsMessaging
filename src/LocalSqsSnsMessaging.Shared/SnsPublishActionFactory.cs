namespace LocalSqsSnsMessaging;

public static class SnsPublishActionFactory
{
    public static void UpdateTopicPublishAction(string topicArn, InMemoryAwsBus bus)
    {
        ArgumentNullException.ThrowIfNull(topicArn);
        ArgumentNullException.ThrowIfNull(bus);

        if (!bus.TryGetTopicByArn(topicArn, out var topic))
        {
            throw new InvalidOperationException($"Topic not found: {topicArn}");
        }

        // Endpoints are resolved at publish time rather than captured here, so a queue deleted
        // after subscribing is treated as a delivery failure (and dead-lettered if configured)
        // instead of silently receiving messages through a stale reference.
        var subscriptions = bus.RegionOfArn(topicArn).Subscriptions.Values
            .Where(s => s.TopicArn == topicArn && !s.PendingConfirmation)
            .ToList();

        topic.PublishAction = new SnsPublishAction(subscriptions, topic, bus);
    }

    /// <summary>
    /// Brings <paramref name="subscription"/> into its topic's fan-out (once it has been added to the
    /// bus, or confirmed), then replays to it the archived messages its <c>ReplayPolicy</c> selects.
    /// On a FIFO topic both happen under the topic's gate, so no publish lands between the replayed
    /// messages and the live ones that follow them.
    /// </summary>
    internal static void Activate(SnsTopicResource topic, SnsSubscription subscription, InMemoryAwsBus bus)
    {
        if (topic.Fifo is not { } fifo)
        {
            UpdateTopicPublishAction(topic.Arn, bus);
            return;
        }

        lock (fifo.Gate)
        {
            UpdateTopicPublishAction(topic.Arn, bus);
            topic.PublishAction.Replay(subscription);
        }
    }
}
