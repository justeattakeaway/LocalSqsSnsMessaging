#pragma warning disable CA1711
namespace LocalSqsSnsMessaging.Tests.Verification.ExternalVerification;

[InheritsTests]
public class SnsPublishAsyncTestsVerification : SnsPublishAsyncTests
{
    [ClassDataSource<AspireFixture>(Shared = SharedType.PerTestSession)]
    public required AspireFixture AspireFixture { get; init; }

    [Before(Test)]
    public void BeforeEachTest()
    {
#pragma warning disable CA5394
        var rawAccountId = Random.Shared.NextInt64(999999999999).ToString("D12", NumberFormatInfo.InvariantInfo);
#pragma warning restore CA5394
        AccountId = ClientFactory.EffectiveAccountId(rawAccountId);
        Sns = ClientFactory.CreateSnsClient(AccountId, AspireFixture.ServicePort);
        Sqs = ClientFactory.CreateSqsClient(AccountId, AspireFixture.ServicePort);
        SqsForTeardown = Sqs;
        SnsForTeardown = Sns;
    }

    protected override bool SupportsAttributeSizeValidation() => true;
}
