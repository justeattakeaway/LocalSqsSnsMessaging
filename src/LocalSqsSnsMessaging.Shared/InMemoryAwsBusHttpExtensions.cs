#if !ASPNETCORE
using System.ComponentModel;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using LocalSqsSnsMessaging.Http;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Extension methods for creating AWS SDK clients that use the in-memory HTTP message handler.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class InMemoryAwsBusHttpExtensions
{
    /// <param name="bus">The in-memory AWS bus instance.</param>
    extension(InMemoryAwsBus bus)
    {
        /// <summary>
        /// Creates a real AWS SDK SQS client configured to use the in-memory bus via HTTP message handler.
        /// This allows testing code that depends on the concrete AmazonSQSClient type.
        /// </summary>
        /// <returns>An AmazonSQSClient configured with the in-memory handler.</returns>
        public AmazonSQSClient CreateSqsClient()
        {
            ArgumentNullException.ThrowIfNull(bus);

            var handler = new InMemoryAwsHttpMessageHandler(bus, AwsServiceType.Sqs);
            var httpClientFactory = new InMemoryHttpClientFactory(handler);

            var config = new AmazonSQSConfig
            {
                ServiceURL = $"https://sqs.{bus.CurrentRegion}.amazonaws.com",
                AuthenticationRegion = bus.CurrentRegion,
                UseHttp = false,
                MaxErrorRetry = 0, // Disable retries for in-memory testing
                HttpClientFactory = httpClientFactory
            };

            // Use anonymous credentials since we're not actually calling AWS
            var credentials = new AnonymousAWSCredentials();

            return new AmazonSQSClient(credentials, config);
        }

#if !AWS_SDK_V3
        /// <summary>
        /// Creates a SQS client backed by the in-memory bus that exposes the
        /// <see cref="RawAmazonSQSClient.ReceiveMessageRawAsync"/> overload in
        /// addition to the standard <see cref="AmazonSQSClient"/> surface. The
        /// raw overload returns each message body as
        /// <see cref="ReadOnlyMemory{Byte}"/> UTF-8 bytes for caller-side
        /// deserialization.
        /// </summary>
        public RawAmazonSQSClient CreateRawSqsClient()
        {
            ArgumentNullException.ThrowIfNull(bus);

            var handler = new InMemoryAwsHttpMessageHandler(bus, AwsServiceType.Sqs);
            var httpClientFactory = new InMemoryHttpClientFactory(handler);

            var config = new AmazonSQSConfig
            {
                ServiceURL = $"https://sqs.{bus.CurrentRegion}.amazonaws.com",
                AuthenticationRegion = bus.CurrentRegion,
                UseHttp = false,
                MaxErrorRetry = 0,
                HttpClientFactory = httpClientFactory
            };

            var credentials = new AnonymousAWSCredentials();
            return new RawAmazonSQSClient(credentials, config);
        }
#endif

        /// <summary>
        /// Creates a real AWS SDK SNS client configured to use the in-memory bus via HTTP message handler.
        /// This allows testing code that depends on the concrete AmazonSimpleNotificationServiceClient type.
        /// </summary>
        /// <returns>An AmazonSimpleNotificationServiceClient configured with the in-memory handler.</returns>
        public AmazonSimpleNotificationServiceClient CreateSnsClient()
        {
            ArgumentNullException.ThrowIfNull(bus);

            var handler = new InMemoryAwsHttpMessageHandler(bus, AwsServiceType.Sns);
            var httpClientFactory = new InMemoryHttpClientFactory(handler);

            var config = new AmazonSimpleNotificationServiceConfig
            {
                ServiceURL = $"https://sns.{bus.CurrentRegion}.amazonaws.com",
                AuthenticationRegion = bus.CurrentRegion,
                UseHttp = false,
                MaxErrorRetry = 0, // Disable retries for in-memory testing
                HttpClientFactory = httpClientFactory
            };

            // Use anonymous credentials since we're not actually calling AWS
            var credentials = new AnonymousAWSCredentials();

            return new AmazonSimpleNotificationServiceClient(credentials, config);
        }
    }
}
#endif
