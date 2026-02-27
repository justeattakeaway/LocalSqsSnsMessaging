namespace LocalSqsSnsMessaging.Tests.Verification.MotoServer;

[InheritsTests]
[NotInParallel(Order = 2)]
public class SqsChangeMessageVisibilityAsyncMotoServerTests : SqsChangeMessageVisibilityAsyncTests
{
    private const string MotoDefaultAccountId = "123456789012";

    [ClassDataSource<AspireFixture>(Shared = SharedType.PerTestSession)]
    public required AspireFixture AspireFixture { get; set; }

    [Before(Test)]
    public async Task BeforeEachTest()
    {
        await AspireFixture.ResetMotoStateAsync();
        Console.WriteLine($"AccountId: {MotoDefaultAccountId}");
        Sqs = ClientFactory.CreateSqsClient(MotoDefaultAccountId, AspireFixture.MotoPort!.Value);
    }

    [Test, Skip("Moto Server handles non-in-flight message visibility differently")]
    public new Task ChangeMessageVisibilityAsync_MessageNotInFlight_ThrowsException(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
