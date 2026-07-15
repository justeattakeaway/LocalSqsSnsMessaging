namespace LocalSqsSnsMessaging.Tests.Verification.ExternalVerification;

[InheritsTests]
public class EventBridgeRoutingTestsVerification : EventBridgeRoutingTests
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
        EventBridge = ClientFactory.CreateEventBridgeClient(AccountId, AspireFixture.ServicePort);
        Sqs = ClientFactory.CreateSqsClient(AccountId, AspireFixture.ServicePort);
        SqsForTeardown = Sqs;
    }
}
