namespace LocalSqsSnsMessaging.Tests.Verification.MotoServer;

[InheritsTests]
[NotInParallel(Order = 4)]
public class SqsFifoTestsMotoServer : SqsFifoTests
{
    private const string MotoDefaultAccountId = "123456789012";

    [ClassDataSource<AspireFixture>(Shared = SharedType.PerTestSession)]
    public required AspireFixture AspireFixture { get; set; }

    [Before(Test)]
    public async Task BeforeEachTest()
    {
        await AspireFixture.ResetMotoStateAsync();
        AccountId = MotoDefaultAccountId;
        Console.WriteLine($"AccountId: {AccountId}");
        Sqs = ClientFactory.CreateSqsClient(AccountId, AspireFixture.MotoPort!.Value);
    }
}
