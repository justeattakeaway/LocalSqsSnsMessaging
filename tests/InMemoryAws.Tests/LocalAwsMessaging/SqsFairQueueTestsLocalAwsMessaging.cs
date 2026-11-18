namespace InMemoryAws.Tests.LocalAwsMessaging;

[InheritsTests]
public class SqsFairQueueTestsLocalAwsMessaging : SqsFairQueueTests
{
    public SqsFairQueueTestsLocalAwsMessaging()
    {
        var cloud = new InMemoryAwsCloud();
        AccountId = cloud.CurrentAccountId;
        Sqs = cloud.CreateSqsClient();
    }
}
