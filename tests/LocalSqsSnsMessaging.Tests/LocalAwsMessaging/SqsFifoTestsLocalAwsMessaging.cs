using Microsoft.Extensions.Time.Testing;

namespace LocalSqsSnsMessaging.Tests.LocalAwsMessaging;

[InheritsTests]
public class SqsFifoTestsLocalAwsMessaging : SqsFifoTests
{
    public SqsFifoTestsLocalAwsMessaging()
    {
        var bus = new InMemoryAwsBus();
        AccountId = bus.CurrentAccountId;
        Sqs = bus.CreateSqsClient();
    }
}
