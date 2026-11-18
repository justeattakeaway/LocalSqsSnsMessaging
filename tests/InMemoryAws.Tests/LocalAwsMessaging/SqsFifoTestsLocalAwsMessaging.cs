using Microsoft.Extensions.Time.Testing;

namespace InMemoryAws.Tests.LocalAwsMessaging;

[InheritsTests]
public class SqsFifoTestsLocalAwsMessaging : SqsFifoTests
{
    public SqsFifoTestsLocalAwsMessaging()
    {
        TimeProvider = new FakeTimeProvider();
        var cloud = new InMemoryAwsCloud
        {
            TimeProvider = TimeProvider
        };
        AccountId = cloud.CurrentAccountId;
        Sqs = cloud.CreateSqsClient();
    }

    protected override bool RejectsAFifoBatchWithAnInvalidEntryWhole() => true;
}
