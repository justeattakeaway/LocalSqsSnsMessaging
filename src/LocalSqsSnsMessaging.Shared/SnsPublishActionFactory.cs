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

        var subscriptionsAndQueues = bus.Subscriptions.Values
            .Where(s => s.TopicArn == topicArn)
            .Select(subscription => (
                Subscription: subscription,
                Queue: bus.Queues.GetValueOrDefault(GetNameFromArn(subscription.EndPoint))
            ))
            .Where(x => x.Queue is not null)
            .ToList();

        topic.PublishAction = new SnsPublishAction(subscriptionsAndQueues!, bus.TimeProvider);
    }

    private static string GetNameFromArn(string arn) => arn.Split(':').Last();
}