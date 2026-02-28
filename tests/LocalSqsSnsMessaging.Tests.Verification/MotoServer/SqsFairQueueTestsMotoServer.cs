namespace LocalSqsSnsMessaging.Tests.Verification.MotoServer;

[InheritsTests]
[NotInParallel(Order = 3)]
public class SqsFairQueueTestsMotoServer : SqsFairQueueTests
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

    [Test, Skip("Moto Server does not scope FIFO deduplication to message groups")]
    public new Task FairQueue_DeduplicationScopedToMessageGroup(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
