using Microsoft.Extensions.Time.Testing;

namespace InMemoryAws.Tests.LocalAwsMessaging;

[InheritsTests]
public class SqsQueueTagsLocalAwsMessagingTests : SqsQueueTagsTests
{
    public SqsQueueTagsLocalAwsMessagingTests()
    {
        var cloud = new InMemoryAwsCloud();
        Sqs = cloud.CreateSqsClient();
    }
}
