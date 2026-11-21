namespace Shared;

public static class Fixture
{
    public static Amazon.SQS.IAmazonSQS CreateSqs(InMemoryAws.InMemoryAwsCloud cloud) => InMemoryAws.InMemoryAwsSdkExtensions.CreateSqsClient(cloud);
}
