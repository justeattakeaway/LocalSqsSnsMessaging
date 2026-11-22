public static class Clients
{
    public static Amazon.SQS.AmazonSQSClient Sqs() => InMemoryAws.InMemoryAwsSdkExtensions.CreateSqsClient(new InMemoryAws.InMemoryAwsCloud());
}
