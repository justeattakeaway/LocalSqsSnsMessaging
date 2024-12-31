using System.Diagnostics;

namespace LocalSqsSnsMessaging.Tests.Verification.LocalStack;

// ReSharper disable once UnusedType.Global
#pragma warning disable CA1711
[Collection(AspireTestCollection.Name)]
public class SnsPublishAsyncTestsLocalStack : SnsPublishAsyncTests
#pragma warning restore CA1711
{
    public SnsPublishAsyncTestsLocalStack(AspireFixture aspireFixture, ITestOutputHelper testOutputHelper) : base(testOutputHelper)
    {
        ArgumentNullException.ThrowIfNull(aspireFixture);
        
#pragma warning disable CA5394
        AccountId = Random.Shared.NextInt64(999999999999).ToString("D12", NumberFormatInfo.InvariantInfo);
#pragma warning restore CA5394
        Debug.Assert(testOutputHelper != null);
        testOutputHelper.WriteLine($"AccountId: {AccountId}");
        Sns = ClientFactory.CreateSnsClient(AccountId, aspireFixture.LocalStackPort!.Value);
        Sqs = ClientFactory.CreateSqsClient(AccountId, aspireFixture.LocalStackPort!.Value);    
    }

    protected override bool SupportsAttributeSizeValidation() => true;

    protected override Task WaitAsync(TimeSpan delay) => Task.Delay(delay);
}