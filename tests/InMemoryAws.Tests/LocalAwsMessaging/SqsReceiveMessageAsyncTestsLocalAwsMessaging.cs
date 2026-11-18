using Microsoft.Extensions.Time.Testing;

namespace InMemoryAws.Tests.LocalAwsMessaging;

[InheritsTests]
public class SqsReceiveMessageAsyncTestsLocalAwsMessaging : SqsReceiveMessageAsyncTests
{
    public SqsReceiveMessageAsyncTestsLocalAwsMessaging()
    {
        TimeProvider = new FakeTimeProvider();
        var cloud = new InMemoryAwsCloud
        {
            TimeProvider = TimeProvider
        };
        AccountId = cloud.CurrentAccountId;
        Sqs = cloud.CreateSqsClient();
    }
}
