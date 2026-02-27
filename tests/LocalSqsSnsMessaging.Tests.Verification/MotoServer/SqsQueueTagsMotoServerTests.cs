namespace LocalSqsSnsMessaging.Tests.Verification.MotoServer;

[InheritsTests]
[NotInParallel(Order = 5)]
public class SqsQueueTagsMotoServerTests : SqsQueueTagsTests
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

    [Test, Skip("Moto Server preserves null tag values instead of stripping them")]
    public new Task TagQueueAsync_NullTagValue_Success(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
