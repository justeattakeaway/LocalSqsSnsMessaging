#if !ASPNETCORE
using System.ComponentModel;
using Amazon.DynamoDBv2;
using Amazon.EventBridge;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using InMemoryAws.Http;

namespace InMemoryAws;

/// <summary>
/// Extension methods for creating AWS SDK clients that use the in-memory HTTP message handler.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class InMemoryAwsCloudHttpExtensions
{
    /// <param name="cloud">The in-memory AWS cloud instance.</param>
    extension(InMemoryAwsCloud cloud)
    {
        /// <summary>
        /// Creates a real AWS SDK SQS client configured to use the in-memory cloud via HTTP message handler.
        /// This allows testing code that depends on the concrete AmazonSQSClient type.
        /// </summary>
        /// <returns>An AmazonSQSClient configured with the in-memory handler.</returns>
        public AmazonSQSClient CreateSqsClient()
        {
            ArgumentNullException.ThrowIfNull(cloud);

            var handler = new InMemoryAwsHttpMessageHandler(cloud, AwsServiceType.Sqs);
            var httpClientFactory = new InMemoryHttpClientFactory(handler);

            var config = new AmazonSQSConfig
            {
                ServiceURL = $"https://sqs.{cloud.CurrentRegion}.amazonaws.com",
                AuthenticationRegion = cloud.CurrentRegion,
                UseHttp = false,
                MaxErrorRetry = 0, // Disable retries for in-memory testing
                HttpClientFactory = httpClientFactory
            };

            // Use anonymous credentials since we're not actually calling AWS
            var credentials = new AnonymousAWSCredentials();

            return new AmazonSQSClient(credentials, config);
        }

        /// <summary>
        /// Creates an <see cref="Amazon.Runtime.HttpClientFactory"/> that routes SQS and SNS traffic
        /// to this in-memory cloud, delegating other AWS services to <paramref name="fallback"/>.
        /// </summary>
        /// <param name="fallback">
        /// Optional delegate invoked for AWS clients not backed by this cloud. Typically a one-liner that
        /// defers to an <c>IHttpClientFactory</c>, e.g.
        /// <c>cfg =&gt; httpClientFactory.CreateClient(cfg.GetType().Name)</c>. When <see langword="null"/>,
        /// requests for unhandled services throw <see cref="NotSupportedException"/>.
        /// </param>
        /// <returns>A factory suitable for use in DI as the application's <c>HttpClientFactory</c>.</returns>
        public InMemoryAwsHttpClientFactory CreateAwsHttpClientFactory(Func<IClientConfig, HttpClient>? fallback = null)
        {
            ArgumentNullException.ThrowIfNull(cloud);
            return new InMemoryAwsHttpClientFactory(cloud, fallback);
        }

        /// <summary>
        /// Creates a real AWS SDK SNS client configured to use the in-memory cloud via HTTP message handler.
        /// This allows testing code that depends on the concrete AmazonSimpleNotificationServiceClient type.
        /// </summary>
        /// <returns>An AmazonSimpleNotificationServiceClient configured with the in-memory handler.</returns>
        public AmazonSimpleNotificationServiceClient CreateSnsClient()
        {
            ArgumentNullException.ThrowIfNull(cloud);

            var handler = new InMemoryAwsHttpMessageHandler(cloud, AwsServiceType.Sns);
            var httpClientFactory = new InMemoryHttpClientFactory(handler);

            var config = new AmazonSimpleNotificationServiceConfig
            {
                ServiceURL = $"https://sns.{cloud.CurrentRegion}.amazonaws.com",
                AuthenticationRegion = cloud.CurrentRegion,
                UseHttp = false,
                MaxErrorRetry = 0, // Disable retries for in-memory testing
                HttpClientFactory = httpClientFactory
            };

            // Use anonymous credentials since we're not actually calling AWS
            var credentials = new AnonymousAWSCredentials();

            return new AmazonSimpleNotificationServiceClient(credentials, config);
        }

        /// <summary>
        /// Creates a real AWS SDK EventBridge client configured to use the in-memory cloud via HTTP message handler.
        /// This allows testing code that depends on the concrete AmazonEventBridgeClient type.
        /// </summary>
        /// <returns>An AmazonEventBridgeClient configured with the in-memory handler.</returns>
        public AmazonEventBridgeClient CreateEventBridgeClient()
        {
            ArgumentNullException.ThrowIfNull(cloud);

            var handler = new InMemoryAwsHttpMessageHandler(cloud, AwsServiceType.EventBridge);
            var httpClientFactory = new InMemoryHttpClientFactory(handler);

            var config = new AmazonEventBridgeConfig
            {
                ServiceURL = $"https://events.{cloud.CurrentRegion}.amazonaws.com",
                AuthenticationRegion = cloud.CurrentRegion,
                UseHttp = false,
                MaxErrorRetry = 0, // Disable retries for in-memory testing
                HttpClientFactory = httpClientFactory
            };

            // Use anonymous credentials since we're not actually calling AWS
            var credentials = new AnonymousAWSCredentials();

            return new AmazonEventBridgeClient(credentials, config);
        }

        /// <summary>
        /// Creates a real AWS SDK DynamoDB client configured to use the in-memory cloud via HTTP message handler.
        /// This allows testing code that depends on the concrete AmazonDynamoDBClient type, and the
        /// SDK's higher-level APIs built on it (<c>DynamoDBContext</c>, <c>Table</c>).
        /// </summary>
        /// <returns>An AmazonDynamoDBClient configured with the in-memory handler.</returns>
        public AmazonDynamoDBClient CreateDynamoDbClient()
        {
            ArgumentNullException.ThrowIfNull(cloud);

            var handler = new InMemoryAwsHttpMessageHandler(cloud, AwsServiceType.DynamoDb);
            var config = new AmazonDynamoDBConfig
            {
                ServiceURL = $"https://dynamodb.{cloud.CurrentRegion}.amazonaws.com",
                AuthenticationRegion = cloud.CurrentRegion,
                UseHttp = false,
                MaxErrorRetry = 0, // Disable retries for in-memory testing
                HttpClientFactory = new InMemoryHttpClientFactory(handler)
            };

            return new AmazonDynamoDBClient(new AnonymousAWSCredentials(), config);
        }
    }
}
#endif
