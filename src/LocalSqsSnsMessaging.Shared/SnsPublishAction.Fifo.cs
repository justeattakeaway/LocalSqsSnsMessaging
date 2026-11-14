namespace LocalSqsSnsMessaging;

/// <summary>Publishing to a FIFO topic, and replaying its archive.</summary>
internal sealed partial class SnsPublishAction
{
    /// <summary>
    /// Publishes to a FIFO topic. The topic deduplicates the message for five minutes (by its
    /// <c>MessageDeduplicationId</c>, or by its body under <c>ContentBasedDeduplication</c>; across the
    /// topic, or only within its message group under <c>FifoThroughputScope=MessageGroup</c>), gives it
    /// a sequence number, archives it and delivers it, all under the topic's gate so every subscription
    /// receives a message group in sequence-number order. A duplicate is accepted but not delivered,
    /// and reports the original's <c>MessageId</c> and <c>SequenceNumber</c>.
    /// </summary>
    private (string MessageId, string? SequenceNumber) PublishToFifoTopic(SnsFifoTopicState fifo, OutboundMessage message)
    {
        lock (fifo.Gate)
        {
            var now = _bus.TimeProvider.GetUtcNow();
            var deduplicationId = message.DeduplicationId
                ?? (fifo.ContentBasedDeduplication ? SqsDeduplicationCache.ContentDeduplicationId(message.Body) : null);
            var sequenced = message with
            {
                DeduplicationId = deduplicationId,
                SequenceNumber = FifoSequenceNumber.Next().ToString(NumberFormatInfo.InvariantInfo),
            };

            if (deduplicationId is not null
                && !fifo.DeduplicationFor(message.MessageGroupId ?? string.Empty)
                    .TryClaim(deduplicationId, sequenced.MessageId, sequenced.SequenceNumber, now, out var original))
            {
                return (original.MessageId, original.SequenceNumber);
            }

            fifo.Archive(sequenced, now);

            // The subscriptions may have changed while this publish waited for the gate: the topic's
            // current action holds the ones subscribed now.
            _topic.PublishAction.Deliver(sequenced);
            return (sequenced.MessageId, sequenced.SequenceNumber);
        }
    }

    /// <summary>
    /// Delivers to <paramref name="subscription"/> the archived messages its <c>ReplayPolicy</c>
    /// selects, in publish order, and marks the replay <c>Completed</c>. Does nothing for a
    /// subscription without a replay policy, or one still pending confirmation.
    /// </summary>
    internal void Replay(SnsSubscription subscription)
    {
        if (_topic.Fifo is not { } fifo || subscription.ReplayPolicy is null || subscription.PendingConfirmation)
        {
            return;
        }

        var (start, end) = SnsReplayPolicy.Parse(subscription.ReplayPolicy);
        lock (fifo.Gate)
        {
            foreach (var message in fifo.Archived(start, end, _bus.TimeProvider.GetUtcNow()))
            {
                DeliverTo(subscription, message);
            }

            subscription.ReplayStatus = SnsReplayPolicy.Completed;
        }
    }
}
