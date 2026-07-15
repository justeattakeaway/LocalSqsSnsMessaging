using Microsoft.Extensions.Time.Testing;

namespace LocalSqsSnsMessaging.Tests.LocalAwsMessaging;

[InheritsTests]
public class EventBridgeRoutingTestsLocalAwsMessaging : EventBridgeRoutingTests
{
    public EventBridgeRoutingTestsLocalAwsMessaging()
    {
        TimeProvider = new FakeTimeProvider();
        var bus = new InMemoryAwsBus
        {
            TimeProvider = TimeProvider
        };
        AccountId = bus.CurrentAccountId;
        EventBridge = bus.CreateEventBridgeClient();
        Sqs = bus.CreateSqsClient();
        SqsForTeardown = Sqs;
    }
}
