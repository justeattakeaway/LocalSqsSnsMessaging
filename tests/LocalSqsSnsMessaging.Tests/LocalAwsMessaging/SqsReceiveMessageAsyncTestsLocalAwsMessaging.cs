using Microsoft.Extensions.Time.Testing;

namespace LocalSqsSnsMessaging.Tests.LocalAwsMessaging;

[InheritsTests]
public class SqsReceiveMessageAsyncTestsLocalAwsMessaging : SqsReceiveMessageAsyncTests
{
    public SqsReceiveMessageAsyncTestsLocalAwsMessaging()
    {
        TimeProvider = new FakeTimeProvider();
        var bus = new InMemoryAwsBus
        {
            TimeProvider = TimeProvider
        };
        AccountId = bus.CurrentAccountId;
        Sqs = bus.CreateSqsClient();
    }
}
