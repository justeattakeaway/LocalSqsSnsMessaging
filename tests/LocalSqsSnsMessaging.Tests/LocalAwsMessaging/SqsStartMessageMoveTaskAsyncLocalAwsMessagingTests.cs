using Microsoft.Extensions.Time.Testing;

namespace LocalSqsSnsMessaging.Tests.LocalAwsMessaging;

[InheritsTests]
public class SqsStartMessageMoveTaskAsyncLocalAwsMessagingTests : SqsStartMessageMoveTaskAsyncTests
{
    public SqsStartMessageMoveTaskAsyncLocalAwsMessagingTests()
    {
        TimeProvider = new FakeTimeProvider();
        var bus = new InMemoryAwsBus
        {
            TimeProvider = TimeProvider
        };
        Sqs = bus.CreateSqsClient();
    }
}
