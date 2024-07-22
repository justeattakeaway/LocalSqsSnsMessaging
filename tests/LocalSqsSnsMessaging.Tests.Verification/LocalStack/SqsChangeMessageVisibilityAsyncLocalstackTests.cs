using System.Diagnostics;

namespace LocalSqsSnsMessaging.Tests.Verification.LocalStack;

// ReSharper disable once UnusedType.Global
[Collection(AspireTestCollection.Name)]
public class SqsChangeMessageVisibilityAsyncLocalStackTests : SqsChangeMessageVisibilityAsyncTests
{
    public SqsChangeMessageVisibilityAsyncLocalStackTests(AspireFixture aspireFixture, ITestOutputHelper testOutputHelper)
    {
        ArgumentNullException.ThrowIfNull(aspireFixture);
        
#pragma warning disable CA5394
        var accountId = Random.Shared.NextInt64(999999999999).ToString("D12", NumberFormatInfo.InvariantInfo);
#pragma warning restore CA5394
        Debug.Assert(testOutputHelper != null);
        testOutputHelper.WriteLine($"AccountId: {accountId}");
        Sqs = ClientFactory.CreateSqsClient(accountId, aspireFixture.LocalStackPort!.Value);
    }

    protected override Task AdvanceTime(TimeSpan timeSpan)
    {
        return Task.Delay(timeSpan);
    }
}