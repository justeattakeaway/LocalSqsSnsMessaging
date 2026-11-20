namespace InMemoryAws.Http;

/// <summary>
/// Maps between the services an <see cref="InMemoryAwsCloud"/> serves and the names and URLs AWS SDK
/// clients know them by, without referencing the AWS SDK. This is what wiring an SDK client to the
/// cloud by hand needs: pick the handler from the client config's <c>RegionEndpointServiceName</c>
/// (<see cref="TryGetServiceType"/>), and point the client at <see cref="GetServiceUrl"/>.
/// </summary>
public static class AwsServiceEndpoints
{
    /// <summary>
    /// The name AWS endpoints use for <paramref name="serviceType"/>, as an AWS SDK client config's
    /// <c>RegionEndpointServiceName</c> reports it: <c>sqs</c>, <c>sns</c>, <c>events</c>, <c>dynamodb</c>,
    /// <c>streams.dynamodb</c> or <c>s3</c>.
    /// </summary>
    /// <param name="serviceType">The service.</param>
    /// <returns>The endpoint service name.</returns>
    public static string GetServiceName(AwsServiceType serviceType) => serviceType switch
    {
        AwsServiceType.Sqs => "sqs",
        AwsServiceType.Sns => "sns",
        AwsServiceType.EventBridge => "events",
        AwsServiceType.DynamoDb => "dynamodb",
        AwsServiceType.DynamoDbStreams => "streams.dynamodb",
        AwsServiceType.S3 => "s3",
        _ => throw new ArgumentOutOfRangeException(nameof(serviceType), serviceType, "Unknown AWS service type."),
    };

    /// <summary>
    /// Finds the service an endpoint service name (an AWS SDK client config's <c>RegionEndpointServiceName</c>)
    /// names, when the cloud serves it.
    /// </summary>
    /// <param name="serviceName">The endpoint service name, such as <c>sqs</c>.</param>
    /// <param name="serviceType">The service, when the cloud serves it.</param>
    /// <returns><see langword="true"/> when the cloud serves <paramref name="serviceName"/>.</returns>
    public static bool TryGetServiceType(string? serviceName, out AwsServiceType serviceType)
    {
        switch (serviceName)
        {
            case "sqs": serviceType = AwsServiceType.Sqs; return true;
            case "sns": serviceType = AwsServiceType.Sns; return true;
            case "events": serviceType = AwsServiceType.EventBridge; return true;
            case "dynamodb": serviceType = AwsServiceType.DynamoDb; return true;
            case "streams.dynamodb": serviceType = AwsServiceType.DynamoDbStreams; return true;
            case "s3": serviceType = AwsServiceType.S3; return true;
            default: serviceType = default; return false;
        }
    }

    /// <summary>
    /// The URL an AWS SDK client for <paramref name="serviceType"/> should use as its <c>ServiceURL</c>: the
    /// service's AWS endpoint in the cloud's <see cref="InMemoryAwsCloud.CurrentRegion"/>, such as
    /// <c>https://sqs.us-east-1.amazonaws.com</c>. Requests never leave the process; the host only tells the
    /// cloud which region an unsigned request is for.
    /// </summary>
    /// <param name="cloud">The cloud the client talks to.</param>
    /// <param name="serviceType">The service.</param>
    /// <returns>The service URL.</returns>
    public static Uri GetServiceUrl(InMemoryAwsCloud cloud, AwsServiceType serviceType)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        return new Uri($"https://{GetServiceName(serviceType)}.{cloud.CurrentRegion}.amazonaws.com");
    }
}
