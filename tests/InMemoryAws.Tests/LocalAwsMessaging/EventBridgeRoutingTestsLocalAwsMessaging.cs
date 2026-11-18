using Microsoft.Extensions.Time.Testing;

namespace InMemoryAws.Tests.LocalAwsMessaging;

[InheritsTests]
public class EventBridgeRoutingTestsLocalAwsMessaging : EventBridgeRoutingTests
{
    public EventBridgeRoutingTestsLocalAwsMessaging()
    {
        TimeProvider = new FakeTimeProvider();
        var cloud = new InMemoryAwsCloud
        {
            TimeProvider = TimeProvider
        };
        AccountId = cloud.CurrentAccountId;
        EventBridge = cloud.CreateEventBridgeClient();
        Sqs = cloud.CreateSqsClient();
        SqsForTeardown = Sqs;
    }
}
