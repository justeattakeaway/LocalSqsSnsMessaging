namespace LocalSqsSnsMessaging.Tests.Verification.LocalStack;

[InheritsTests]
public class SqsChangeMessageVisibilityAsyncLocalStackTests : SqsChangeMessageVisibilityAsyncTests
{
    [ClassDataSource<AspireFixture>(Shared = SharedType.PerTestSession)]
    public required AspireFixture AspireFixture { get; set; }

    [Before(Test)]
    public void BeforeEachTest()
    {
#pragma warning disable CA5394
        var accountId = Random.Shared.NextInt64(999999999999).ToString("D12", NumberFormatInfo.InvariantInfo);
#pragma warning restore CA5394
        Console.WriteLine($"AccountId: {accountId}");
        Sqs = ClientFactory.CreateSqsClient(accountId, AspireFixture.LocalStackPort!.Value);
    }

    protected override Task AdvanceTime(TimeSpan timeSpan)
    {
        return Task.Delay(timeSpan);
    }
}
