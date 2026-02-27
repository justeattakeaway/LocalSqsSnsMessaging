#pragma warning disable CA1711
namespace LocalSqsSnsMessaging.Tests.Verification.MotoServer;

[InheritsTests]
[NotInParallel(Order = 6)]
public class SqsReceiveMessageAsyncTestsMotoServer : SqsReceiveMessageAsyncTests
{
    [ClassDataSource<AspireFixture>(Shared = SharedType.PerTestSession)]
    public required AspireFixture AspireFixture { get; set; }

    [Before(Test)]
    public async Task BeforeEachTest()
    {
        await AspireFixture.ResetMotoStateAsync();
#pragma warning disable CA5394
        AccountId = Random.Shared.NextInt64(999999999999).ToString("D12", NumberFormatInfo.InvariantInfo);
#pragma warning restore CA5394
        Console.WriteLine($"AccountId: {AccountId}");
        Sqs = ClientFactory.CreateSqsClient(AccountId, AspireFixture.MotoPort!.Value);
    }
}
