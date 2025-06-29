using Microsoft.Extensions.Time.Testing;

namespace LocalSqsSnsMessaging.Tests.LocalAwsMessaging;

[InheritsTests]
public class SqsStartMessageMoveTaskAsyncLocalAwsMessagingTests : SqsStartMessageMoveTaskAsyncTests
{
    private readonly FakeTimeProvider _timeProvider;

    public SqsStartMessageMoveTaskAsyncLocalAwsMessagingTests()
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
