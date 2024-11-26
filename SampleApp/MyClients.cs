using System.Runtime.CompilerServices;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Internal;
using Amazon.SQS;
using Amazon.SQS.Internal;

namespace SampleApp;

public static class MyClients
{
    public static AmazonSimpleNotificationServiceClient CreateSnsClient(IAmazonSimpleNotificationService innerClient)
    {
        var config = new AmazonSimpleNotificationServiceConfig
        {
            RetryMode = RequestRetryMode.Standard,
            MaxErrorRetry = 0,
            IgnoreConfiguredEndpointUrls = true,
            ThrottleRetries = false,
            RegionEndpoint = RegionEndpoint.EUWest1,
            DisableLogging = true,
            DefaultConfigurationMode = DefaultConfigurationMode.Standard,
            RequestMinCompressionSizeBytes = 10_240,
            DisableRequestCompression = false
        };
        
        var client = new AmazonSimpleNotificationServiceClient(new AnonymousAWSCredentials(), config);
        
        var runtimePipeline = GetRuntimePipeline(client);

        runtimePipeline.ReplaceHandler<HttpHandler<HttpContent>>(
#pragma warning disable CA2000
            new HttpHandler<HttpContent>(new SnsHttpRequestFactory(innerClient ,client.Config), client));
#pragma warning restore CA2000
        runtimePipeline.ReplaceHandler<AmazonSimpleNotificationServiceEndpointResolver>(new StaticEndpointResolver());
        runtimePipeline.RemoveHandler<Marshaller>();

        return client;
    }
    
    public static AmazonSQSClient CreateSqsClient(IAmazonSQS innerClient)
    {
        var config = new AmazonSQSConfig
        {
            RetryMode = RequestRetryMode.Standard,
            MaxErrorRetry = 0,
            IgnoreConfiguredEndpointUrls = true,
            ThrottleRetries = false,
            RegionEndpoint = RegionEndpoint.EUWest1,
            DisableLogging = true,
            DefaultConfigurationMode = DefaultConfigurationMode.Standard,
            RequestMinCompressionSizeBytes = 10_240,
            DisableRequestCompression = false
        };
        
        var client = new AmazonSQSClient(new AnonymousAWSCredentials(), config);

        var runtimePipeline = GetRuntimePipeline(client);
        
        runtimePipeline.ReplaceHandler<HttpHandler<HttpContent>>(
#pragma warning disable CA2000
            new HttpHandler<HttpContent>(new SqsHttpRequestFactory(innerClient, client.Config), client));
#pragma warning restore CA2000
        runtimePipeline.ReplaceHandler<AmazonSQSEndpointResolver>(new StaticEndpointResolver());
        runtimePipeline.RemoveHandler<Marshaller>();

        return client;
    }
    
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RuntimePipeline")]
    private static extern RuntimePipeline GetRuntimePipeline(AmazonServiceClient client);
}