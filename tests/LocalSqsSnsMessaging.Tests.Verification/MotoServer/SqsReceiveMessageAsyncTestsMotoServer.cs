#pragma warning disable CA1711
namespace LocalSqsSnsMessaging.Tests.Verification.MotoServer;

[InheritsTests]
[NotInParallel(Order = 6)]
public class SqsReceiveMessageAsyncTestsMotoServer : SqsReceiveMessageAsyncTests
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

    [Test, Skip("Moto Server returns IAM user ID as SenderId, not the access key")]
    public new Task ReceiveMessageAsync_MultipleMessages_CorrectAttributesReturnedForEach(CancellationToken cancellationToken)
        => Task.CompletedTask;

    [Test, Skip("Moto Server returns IAM user ID as SenderId, not the access key")]
    public new Task ReceiveMessageAsync_AllMessageSystemAttributes_AllAttributesReturned(CancellationToken cancellationToken)
        => Task.CompletedTask;

    [Test, Skip("Moto Server returns IAM user ID as SenderId, not the access key")]
    public new Task ReceiveMessageAsync_SpecificMessageSystemAttributes_OnlyRequestedAttributesReturned(CancellationToken cancellationToken)
        => Task.CompletedTask;

    [Test, Skip("Moto Server does not enforce message attribute size limits")]
    public new Task SendMessageAsync_MessageAttributeFullSizeCalculation_ThrowsException(CancellationToken cancellationToken)
        => Task.CompletedTask;

    [Test, Skip("Moto Server does not enforce message attribute size limits")]
    public new Task SendMessageAsync_CustomAttributeTypeNames_CountTowardsLimit(CancellationToken cancellationToken)
        => Task.CompletedTask;

    [Test, Skip("Moto Server does not enforce batch message size limits")]
    public new Task SendMessageAsync_BatchWithAttributeSizeLimits_PartialBatchFailure(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
