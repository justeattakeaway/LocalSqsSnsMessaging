using Amazon.DynamoDBStreams;
using Amazon.DynamoDBv2;
using Amazon.EventBridge;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using InMemoryAws.Http;
using Shouldly;

namespace InMemoryAws.Tests.SdkClient;

/// <summary>
/// <see cref="AwsServiceEndpoints"/>: the SDK-free mapping a client wired by hand relies on, checked against the
/// names the AWS SDK's own client configs report.
/// </summary>
public sealed class AwsServiceEndpointsTests
{
    public static IEnumerable<Func<(AwsServiceType, ClientConfig)>> ServicesAndTheirSdkConfigs()
    {
        yield return () => (AwsServiceType.Sqs, new AmazonSQSConfig());
        yield return () => (AwsServiceType.Sns, new AmazonSimpleNotificationServiceConfig());
        yield return () => (AwsServiceType.EventBridge, new AmazonEventBridgeConfig());
        yield return () => (AwsServiceType.DynamoDb, new AmazonDynamoDBConfig());
        yield return () => (AwsServiceType.DynamoDbStreams, new AmazonDynamoDBStreamsConfig());
        yield return () => (AwsServiceType.S3, new AmazonS3Config());
    }

    [Test]
    [MethodDataSource(nameof(ServicesAndTheirSdkConfigs))]
    public void TheServiceNameIsTheOneTheSdkConfigReports(AwsServiceType serviceType, ClientConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        AwsServiceEndpoints.GetServiceName(serviceType).ShouldBe(config.RegionEndpointServiceName);
        AwsServiceEndpoints.TryGetServiceType(config.RegionEndpointServiceName, out var found).ShouldBeTrue();
        found.ShouldBe(serviceType);
    }

    [Test]
    [Arguments("lambda")]
    [Arguments("SQS")]
    [Arguments("")]
    [Arguments(null)]
    public void AServiceTheCloudDoesNotServeIsNotFound(string? serviceName)
    {
        AwsServiceEndpoints.TryGetServiceType(serviceName, out _).ShouldBeFalse();
    }

    [Test]
    public void TheServiceUrlIsTheServicesEndpointInTheCloudsCurrentRegion()
    {
        var cloud = new InMemoryAwsCloud { CurrentRegion = "eu-west-1" };

        AwsServiceEndpoints.GetServiceUrl(cloud, AwsServiceType.Sqs).ShouldBe(new Uri("https://sqs.eu-west-1.amazonaws.com"));
        AwsServiceEndpoints.GetServiceUrl(cloud, AwsServiceType.DynamoDbStreams).ShouldBe(new Uri("https://streams.dynamodb.eu-west-1.amazonaws.com"));
    }

    [Test]
    public void AnUnknownServiceTypeIsRefused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => AwsServiceEndpoints.GetServiceName((AwsServiceType)42));
        Should.Throw<ArgumentNullException>(() => AwsServiceEndpoints.GetServiceUrl(null!, AwsServiceType.Sqs));
    }
}
