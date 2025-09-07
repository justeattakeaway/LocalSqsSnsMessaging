using Microsoft.Extensions.Time.Testing;

namespace LocalSqsSnsMessaging.Tests.LocalAwsMessaging;

[InheritsTests]
public class SqsQueueTagsLocalAwsMessagingTests : SqsQueueTagsTests
{
    public SqsQueueTagsLocalAwsMessagingTests()
    {
        var bus = new InMemoryAwsBus();
        Sqs = bus.CreateSqsClient();
    }
}
