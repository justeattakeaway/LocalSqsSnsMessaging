namespace LocalSqsSnsMessaging.Tests.LocalAwsMessaging;

[InheritsTests]
public class SqsFairQueueTestsLocalAwsMessaging : SqsFairQueueTests
{
    public SqsFairQueueTestsLocalAwsMessaging()
    {
        var bus = new InMemoryAwsBus();
        AccountId = bus.CurrentAccountId;
        Sqs = bus.CreateSqsClient();
    }
}
