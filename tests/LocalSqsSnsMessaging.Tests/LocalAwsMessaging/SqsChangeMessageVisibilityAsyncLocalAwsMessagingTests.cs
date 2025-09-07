using Microsoft.Extensions.Time.Testing;

namespace LocalSqsSnsMessaging.Tests.LocalAwsMessaging;

[InheritsTests]
public class SqsChangeMessageVisibilityAsyncLocalAwsMessagingTests : SqsChangeMessageVisibilityAsyncTests
{
    public SqsChangeMessageVisibilityAsyncLocalAwsMessagingTests()
    {
        TimeProvider = new FakeTimeProvider();
        var bus = new InMemoryAwsBus
        {
            TimeProvider = TimeProvider
        };
        Sqs = bus.CreateSqsClient();
    }
}
