using Microsoft.Extensions.Time.Testing;

namespace InMemoryAws.Tests.LocalAwsMessaging;

[InheritsTests]
public class SqsStartMessageMoveTaskAsyncLocalAwsMessagingTests : SqsStartMessageMoveTaskAsyncTests
{
    public SqsStartMessageMoveTaskAsyncLocalAwsMessagingTests()
    {
        TimeProvider = new FakeTimeProvider();
        var cloud = new InMemoryAwsCloud
        {
            TimeProvider = TimeProvider
        };
        Sqs = cloud.CreateSqsClient();
    }
}
