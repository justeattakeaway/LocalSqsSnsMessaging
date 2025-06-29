namespace LocalSqsSnsMessaging.Tests.LocalAwsMessaging;

[InheritsTests]
public class SnsPublishAsyncTestsLocalAwsMessaging : SnsPublishAsyncTests
{
    public SnsPublishAsyncTestsLocalAwsMessaging()
    {
        var bus = new InMemoryAwsBus();
        AccountId = bus.CurrentAccountId;
        Sns = bus.CreateSnsClient();
        Sqs = bus.CreateSqsClient();
    }

    protected override bool SupportsAttributeSizeValidation() => false;

    protected override Task WaitAsync(TimeSpan delay) => Task.CompletedTask;
}
