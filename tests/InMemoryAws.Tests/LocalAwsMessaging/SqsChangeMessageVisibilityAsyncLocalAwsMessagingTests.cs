using Microsoft.Extensions.Time.Testing;

namespace InMemoryAws.Tests.LocalAwsMessaging;

[InheritsTests]
public class SqsChangeMessageVisibilityAsyncLocalAwsMessagingTests : SqsChangeMessageVisibilityAsyncTests
{
    public SqsChangeMessageVisibilityAsyncLocalAwsMessagingTests()
    {
        TimeProvider = new FakeTimeProvider();
        var cloud = new InMemoryAwsCloud
        {
            TimeProvider = TimeProvider
        };
        Sqs = cloud.CreateSqsClient();
    }
}
