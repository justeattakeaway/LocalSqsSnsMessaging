namespace LocalSqsSnsMessaging.Tests.Verification.MotoServer;

[InheritsTests]
[NotInParallel(Order = 2)]
public class SqsChangeMessageVisibilityAsyncMotoServerTests : SqsChangeMessageVisibilityAsyncTests
{
    [ClassDataSource<AspireFixture>(Shared = SharedType.PerTestSession)]
    public required AspireFixture AspireFixture { get; set; }

    [Before(Test)]
    public async Task BeforeEachTest()
    {
        await AspireFixture.ResetMotoStateAsync();
#pragma warning disable CA5394
        var accountId = Random.Shared.NextInt64(999999999999).ToString("D12", NumberFormatInfo.InvariantInfo);
#pragma warning restore CA5394
        Console.WriteLine($"AccountId: {accountId}");
        Sqs = ClientFactory.CreateSqsClient(accountId, AspireFixture.MotoPort!.Value);
    }
}
