using Microsoft.Extensions.Time.Testing;

namespace LocalSqsSnsMessaging.Tests.LocalAwsMessaging;

[InheritsTests]
public class SnsPublishAsyncTestsLocalAwsMessaging : SnsPublishAsyncTests
{
    public SnsPublishAsyncTestsLocalAwsMessaging()
    {
        TimeProvider = new FakeTimeProvider();
        var bus = new InMemoryAwsBus
        {
            TimeProvider = TimeProvider
        };
        AccountId = bus.CurrentAccountId;
        Sns = bus.CreateSnsClient();
        Sqs = bus.CreateSqsClient();
    }

    protected override bool SupportsAttributeSizeValidation() => false;
}
