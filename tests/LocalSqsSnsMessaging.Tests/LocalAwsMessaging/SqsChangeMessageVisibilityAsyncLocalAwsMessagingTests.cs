using Microsoft.Extensions.Time.Testing;

namespace LocalSqsSnsMessaging.Tests.LocalAwsMessaging;

// ReSharper disable once UnusedType.Global
public class SqsChangeMessageVisibilityAsyncLocalAwsMessagingTests : SqsChangeMessageVisibilityAsyncTests
{
    private readonly FakeTimeProvider _timeProvider;
    
    public SqsChangeMessageVisibilityAsyncLocalAwsMessagingTests()
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