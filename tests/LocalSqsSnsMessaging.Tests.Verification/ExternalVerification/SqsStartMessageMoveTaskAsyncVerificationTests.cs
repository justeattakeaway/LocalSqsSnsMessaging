namespace LocalSqsSnsMessaging.Tests.Verification.ExternalVerification;

[InheritsTests]
public class SqsStartMessageMoveTaskAsyncVerificationTests : SqsStartMessageMoveTaskAsyncTests
{
    [ClassDataSource<AspireFixture>(Shared = SharedType.PerTestSession)]
    public required AspireFixture AspireFixture { get; set; }

    [Before(Test)]
    public void BeforeEachTest()
    {
#pragma warning disable CA5394
        var rawAccountId = Random.Shared.NextInt64(999999999999).ToString("D12", NumberFormatInfo.InvariantInfo);
#pragma warning restore CA5394
        AccountId = ClientFactory.EffectiveAccountId(rawAccountId);
        Sqs = ClientFactory.CreateSqsClient(AccountId, AspireFixture.ServicePort);
        SqsForTeardown = Sqs;
    }
}
