using Microsoft.Extensions.Time.Testing;

namespace LocalSqsSnsMessaging.Tests.LocalAwsMessaging;

[InheritsTests]
public class SqsQueueTagsLocalAwsMessagingTests : SqsQueueTagsTests
{
    private readonly FakeTimeProvider _timeProvider;

    public SqsQueueTagsLocalAwsMessagingTests()
    {
        _timeProvider = new FakeTimeProvider();
        var bus = new InMemoryAwsBus
        {
            TimeProvider = _timeProvider
        };
        Sqs = bus.CreateSqsClient();
    }

    protected override Task AdvanceTime(TimeSpan timeSpan)
    {
        _timeProvider.Advance(timeSpan);
        return Task.CompletedTask;
    }
}
