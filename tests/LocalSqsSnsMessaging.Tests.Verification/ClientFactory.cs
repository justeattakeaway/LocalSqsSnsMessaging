using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;

namespace LocalSqsSnsMessaging.Tests.Verification;
#pragma warning disable CA1724
public static class ClientFactory
#pragma warning restore CA1724
{
    private static readonly CompositeFormat ServiceUrlFormatString = CompositeFormat.Parse("http://localhost:{0}");

    public static IAmazonSQS CreateSqsClient(string accountId, int localstackPort)
    {
        return new AmazonSQSClient(
            new BasicAWSCredentials(accountId, "shh"),
            new AmazonSQSConfig
            {
                ServiceURL = string.Format(CultureInfo.InvariantCulture, ServiceUrlFormatString, localstackPort)
            });
    }
    
    public static IAmazonSimpleNotificationService CreateSnsClient(string accountId, int localstackPort)
    {
        return new AmazonSimpleNotificationServiceClient(
            new BasicAWSCredentials(accountId, "shh"),
            new AmazonSimpleNotificationServiceConfig
            {
                ServiceURL = string.Format(CultureInfo.InvariantCulture, ServiceUrlFormatString, localstackPort)
            });
    }
}