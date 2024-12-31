namespace LocalSqsSnsMessaging.Tests.LocalAwsMessaging;

// ReSharper disable once UnusedType.Global
public class SnsPublishAsyncTestsLocalAwsMessaging : SnsPublishAsyncTests
{
    public SnsPublishAsyncTestsLocalAwsMessaging(ITestOutputHelper testOutputHelper) : base(testOutputHelper)
    {
        var bus = new InMemoryAwsBus();
        AccountId = bus.CurrentAccountId;
        Sns = bus.CreateSnsClient();
        Sqs = bus.CreateSqsClient();
    }

    protected override bool SupportsAttributeSizeValidation() => false;
    
    protected override Task WaitAsync(TimeSpan delay) => Task.CompletedTask;
}