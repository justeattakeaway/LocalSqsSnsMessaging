using Microsoft.Extensions.Time.Testing;

namespace LocalSqsSnsMessaging.Tests.LocalAwsMessaging;

// ReSharper disable once UnusedType.Global
public class SqsReceiveMessageAsyncTestsLocalAwsMessaging : SqsReceiveMessageAsyncTests
{
    private readonly FakeTimeProvider _timeProvider;
    
    public SqsReceiveMessageAsyncTestsLocalAwsMessaging()
    {
        _timeProvider = new FakeTimeProvider();
        var bus = new InMemoryAwsBus
        {
            TimeProvider = _timeProvider
        };
        AccountId = bus.CurrentAccountId;
        Sqs = bus.CreateSqsClient();
    }

    protected override async Task AdvanceTime(TimeSpan timeSpan)
    {
        _timeProvider.Advance(timeSpan);
        // Allow for continuations to complete
        await Task.Delay(TimeSpan.FromMilliseconds(2));
    }
}