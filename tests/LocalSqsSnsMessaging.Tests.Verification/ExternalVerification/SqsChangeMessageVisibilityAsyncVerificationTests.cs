namespace LocalSqsSnsMessaging.Tests.Verification.ExternalVerification;

[InheritsTests]
public class SqsChangeMessageVisibilityAsyncVerificationTests : SqsChangeMessageVisibilityAsyncTests
{
    [ClassDataSource<AspireFixture>(Shared = SharedType.PerTestSession)]
    public required AspireFixture AspireFixture { get; set; }

    [Before(Test)]
    public void BeforeEachTest()
    {
#pragma warning disable CA5394
        var rawAccountId = Random.Shared.NextInt64(999999999999).ToString("D12", NumberFormatInfo.InvariantInfo);
#pragma warning restore CA5394
        var accountId = ClientFactory.EffectiveAccountId(rawAccountId);
        Sqs = ClientFactory.CreateSqsClient(accountId, AspireFixture.ServicePort);
        SqsForTeardown = Sqs;
    }
}
