using Microsoft.Extensions.Time.Testing;

namespace LocalSqsSnsMessaging.Tests.LocalAwsMessaging;

// ReSharper disable once UnusedType.Global
public class SqsFifoTestsLocalAwsMessaging : SqsFifoTests
{
    private readonly FakeTimeProvider _timeProvider;
    
    public SqsFifoTestsLocalAwsMessaging()
    {
        _timeProvider = new FakeTimeProvider();
        var bus = new InMemoryAwsBus
        {
            TimeProvider = _timeProvider
        };
        AccountId = bus.CurrentAccountId;
        Sqs = bus.CreateSqsClient();
    }

    protected override Task AdvanceTime(TimeSpan timeSpan)
    {
        _timeProvider.Advance(timeSpan);
        return Task.CompletedTask;
    }
}