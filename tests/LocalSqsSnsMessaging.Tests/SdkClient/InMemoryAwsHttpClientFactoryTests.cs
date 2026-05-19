using System.Reflection;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Amazon.SQS.Model;
using LocalSqsSnsMessaging.Http;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.SdkClient;

/// <summary>
/// Tests for <see cref="InMemoryAwsHttpClientFactory"/>, the public hook for routing SQS/SNS
/// traffic to the in-memory bus while delegating other AWS services to a fallback.
/// </summary>
public sealed class InMemoryAwsHttpClientFactoryTests
{
    [Test]
    public async Task RoutesSqsTrafficToBus()
    {
        var bus = new InMemoryAwsBus();
        using var factory = new InMemoryAwsHttpClientFactory(bus);

        using var sqs = new AmazonSQSClient(
            new AnonymousAWSCredentials(),
            new AmazonSQSConfig
            {
                ServiceURL = $"https://sqs.{bus.CurrentRegion}.amazonaws.com",
                AuthenticationRegion = bus.CurrentRegion,
                MaxErrorRetry = 0,
                HttpClientFactory = factory
            });

        var created = await sqs.CreateQueueAsync("via-factory");
        created.QueueUrl.ShouldContain("via-factory");

        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = created.QueueUrl,
            MessageBody = "hi"
        });

        var received = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = created.QueueUrl,
            MaxNumberOfMessages = 1
        });

        received.Messages.ShouldHaveSingleItem();
        received.Messages[0].Body.ShouldBe("hi");
    }

    [Test]
    public async Task RoutesSnsTrafficToBus()
    {
        var bus = new InMemoryAwsBus();
        using var factory = new InMemoryAwsHttpClientFactory(bus);

        using var sns = new AmazonSimpleNotificationServiceClient(
            new AnonymousAWSCredentials(),
            new AmazonSimpleNotificationServiceConfig
            {
                ServiceURL = $"https://sns.{bus.CurrentRegion}.amazonaws.com",
                AuthenticationRegion = bus.CurrentRegion,
                MaxErrorRetry = 0,
                HttpClientFactory = factory
            });

        var topic = await sns.CreateTopicAsync("via-factory");
        topic.TopicArn.ShouldContain("via-factory");
    }

    [Test]
    public void ReportsInMemoryConfigUniqueStringForHandledServices()
    {
        var bus = new InMemoryAwsBus();
        using var factory = new InMemoryAwsHttpClientFactory(bus);

        factory.GetConfigUniqueString(new AmazonSQSConfig()).ShouldBe("InMemory");
        factory.GetConfigUniqueString(new AmazonSimpleNotificationServiceConfig()).ShouldBe("InMemory");
    }

    [Test]
    public void DoesNotCacheOrDisposeInMemoryClients()
    {
        var bus = new InMemoryAwsBus();
        using var factory = new InMemoryAwsHttpClientFactory(bus);

        factory.UseSDKHttpClientCaching(new AmazonSQSConfig()).ShouldBeFalse();
        factory.DisposeHttpClientsAfterUse(new AmazonSQSConfig()).ShouldBeFalse();
        factory.UseSDKHttpClientCaching(new AmazonSimpleNotificationServiceConfig()).ShouldBeFalse();
        factory.DisposeHttpClientsAfterUse(new AmazonSimpleNotificationServiceConfig()).ShouldBeFalse();
    }

    [Test]
    public void DelegatesLifecycleDecisionsToBaseForUnhandledServices()
    {
        var bus = new InMemoryAwsBus();
        using var fallbackClient = new HttpClient();
        using var factoryWithFallback = new InMemoryAwsHttpClientFactory(bus, _ => fallbackClient);
        var baseline = new BaselineFactory();

        var unknownConfig = DispatchProxy.Create<IClientConfig, ClientConfigProxy>();

        // For non-SQS/SNS configs, the factory must match the SDK's default behavior so the
        // fallback's HttpClients are disposed and cached as the SDK normally would.
        factoryWithFallback.DisposeHttpClientsAfterUse(unknownConfig)
            .ShouldBe(baseline.DisposeHttpClientsAfterUse(unknownConfig));
        factoryWithFallback.UseSDKHttpClientCaching(unknownConfig)
            .ShouldBe(baseline.UseSDKHttpClientCaching(unknownConfig));
    }

    private sealed class BaselineFactory : Amazon.Runtime.HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => throw new NotSupportedException();
    }

    [Test]
    public void ThrowsAfterDispose()
    {
        var bus = new InMemoryAwsBus();
        var factory = new InMemoryAwsHttpClientFactory(bus);
        factory.Dispose();

        Should.Throw<ObjectDisposedException>(() => factory.CreateHttpClient(new AmazonSQSConfig()));
        Should.Throw<ObjectDisposedException>(() => factory.GetConfigUniqueString(new AmazonSQSConfig()));
        Should.Throw<ObjectDisposedException>(() => factory.DisposeHttpClientsAfterUse(new AmazonSQSConfig()));
        Should.Throw<ObjectDisposedException>(() => factory.UseSDKHttpClientCaching(new AmazonSQSConfig()));
    }

    [Test]
    public void DelegatesUnhandledServicesToFallback()
    {
        var bus = new InMemoryAwsBus();
        using var fallbackClient = new HttpClient();
        IClientConfig? capturedConfig = null;
        using var factory = new InMemoryAwsHttpClientFactory(bus, cfg =>
        {
            capturedConfig = cfg;
            return fallbackClient;
        });

        var unknownConfig = DispatchProxy.Create<IClientConfig, ClientConfigProxy>();

        var client = factory.CreateHttpClient(unknownConfig);

        client.ShouldBeSameAs(fallbackClient);
        capturedConfig.ShouldBeSameAs(unknownConfig);
    }

    [Test]
    public void ThrowsForUnhandledServicesWhenNoFallback()
    {
        var bus = new InMemoryAwsBus();
        using var factory = new InMemoryAwsHttpClientFactory(bus);

        var unknownConfig = DispatchProxy.Create<IClientConfig, ClientConfigProxy>();

        Should.Throw<NotSupportedException>(() => factory.CreateHttpClient(unknownConfig));
    }

    [Test]
    public async Task CreateAwsHttpClientFactoryExtension_Works()
    {
        var bus = new InMemoryAwsBus();
        using var factory = bus.CreateAwsHttpClientFactory();

        using var sqs = new AmazonSQSClient(
            new AnonymousAWSCredentials(),
            new AmazonSQSConfig
            {
                ServiceURL = $"https://sqs.{bus.CurrentRegion}.amazonaws.com",
                AuthenticationRegion = bus.CurrentRegion,
                MaxErrorRetry = 0,
                HttpClientFactory = factory
            });

        var created = await sqs.CreateQueueAsync("ext-factory");
        created.QueueUrl.ShouldContain("ext-factory");
    }

    public class ClientConfigProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.ReturnType == typeof(void))
            {
                return null;
            }

            if (targetMethod?.ReturnType.IsValueType ?? false)
            {
                return Activator.CreateInstance(targetMethod.ReturnType);
            }

            return null;
        }
    }

}
