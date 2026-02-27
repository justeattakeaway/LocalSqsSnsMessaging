#pragma warning disable CA1711
namespace LocalSqsSnsMessaging.Tests.Verification.MotoServer;

[InheritsTests]
[NotInParallel(Order = 1)]
public class SnsPublishAsyncTestsMotoServer : SnsPublishAsyncTests
{
    // Moto Server maps all unknown access keys to this default account ID.
    private const string MotoDefaultAccountId = "123456789012";

    [ClassDataSource<AspireFixture>(Shared = SharedType.PerTestSession)]
    public required AspireFixture AspireFixture { get; init; }

    [Before(Test)]
    public async Task BeforeEachTest()
    {
        await AspireFixture.ResetMotoStateAsync();
        AccountId = MotoDefaultAccountId;
        Console.WriteLine($"AccountId: {AccountId}");
        Sns = ClientFactory.CreateSnsClient(AccountId, AspireFixture.MotoPort!.Value);
        Sqs = ClientFactory.CreateSqsClient(AccountId, AspireFixture.MotoPort!.Value);
    }

    protected override bool SupportsAttributeSizeValidation() => true;

    [Test, Skip("Moto Server does not enforce message attribute size validation")]
    public new Task PublishAsync_MessageAttributesExceedMaximumSize_ThrowsInvalidParameterException(CancellationToken cancellationToken)
        => Task.CompletedTask;

    [Test, Skip("Moto Server does not enforce message attribute size validation")]
    public new Task PublishAsync_WithSubjectAndMessageAttributes_ExceedsLimit_ThrowsInvalidParameterException(CancellationToken cancellationToken)
        => Task.CompletedTask;

    [Test, Skip("Moto Server does not enforce batch message size limits")]
    public new Task PublishBatchAsync_TotalMessageSizeExceedsLimit_ThrowsBatchRequestTooLongException(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
