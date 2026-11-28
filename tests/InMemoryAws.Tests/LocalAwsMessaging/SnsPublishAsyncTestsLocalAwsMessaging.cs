using Microsoft.Extensions.Time.Testing;

namespace InMemoryAws.Tests.LocalAwsMessaging;

[InheritsTests]
public class SnsPublishAsyncTestsLocalAwsMessaging : SnsPublishAsyncTests
{
    public SnsPublishAsyncTestsLocalAwsMessaging()
    {
        TimeProvider = new FakeTimeProvider();
        var cloud = new InMemoryAwsCloud
        {
            TimeProvider = TimeProvider
        };
        AccountId = cloud.CurrentAccountId;
        Sns = cloud.CreateSnsClient();
        Sqs = cloud.CreateSqsClient();
    }

    protected override bool SupportsAttributeSizeValidation() => true;

    protected override bool ValidatesFilterPolicies() => true;

    protected override bool RefusesCreateTopicWithDifferentTags() => true;
}
