using SampleApp;

namespace LocalSqsSnsMessaging.Tests.LocalAwsMessaging;

// ReSharper disable once UnusedType.Global
public class SnsPublishAsyncTestsLocalAwsMessaging : SnsPublishAsyncTests
{
    public SnsPublishAsyncTestsLocalAwsMessaging(ITestOutputHelper testOutputHelper) : base(testOutputHelper)
    {
        var bus = new InMemoryAwsBus();
        AccountId = bus.CurrentAccountId;
#pragma warning disable CA2000
        Sns = MyClients.CreateSnsClient(bus.CreateSnsClient());
        Sqs = MyClients.CreateSqsClient(bus.CreateSqsClient());
#pragma warning restore CA2000
    }

    protected override Task WaitAsync(TimeSpan delay) => Task.CompletedTask;
}