#pragma warning disable CA1711
namespace LocalSqsSnsMessaging.Tests.Verification.LocalStack;

[InheritsTests]
public class SnsPublishAsyncTestsLocalStack : SnsPublishAsyncTests
{
    [ClassDataSource<AspireFixture>(Shared = SharedType.PerTestSession)]
    public required AspireFixture AspireFixture { get; init; }

    [Before(Test)]
    public void BeforeEachTest()
    {
#pragma warning disable CA5394
        AccountId = Random.Shared.NextInt64(999999999999).ToString("D12", NumberFormatInfo.InvariantInfo);
#pragma warning restore CA5394
        Console.WriteLine($"AccountId: {AccountId}");
        Sns = ClientFactory.CreateSnsClient(AccountId, AspireFixture.LocalStackPort!.Value);
        Sqs = ClientFactory.CreateSqsClient(AccountId, AspireFixture.LocalStackPort!.Value);
    }

    protected override bool SupportsAttributeSizeValidation() => true;
}
