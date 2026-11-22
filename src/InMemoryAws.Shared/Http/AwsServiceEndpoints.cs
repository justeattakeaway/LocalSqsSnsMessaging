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

        // Not checked as a region name: a request that names no region is served from CurrentRegion anyway.
        return ServiceUrl(cloud.CurrentRegion, serviceType);
    }

    /// <summary>
    /// The URL an AWS SDK client for <paramref name="serviceType"/> in <paramref name="region"/> should use as
    /// its <c>ServiceURL</c>, such as <c>https://sqs.eu-west-1.amazonaws.com</c>. The cloud serves an unsigned
    /// request to it from that region's resources (a signed one, from the region it was signed for, so set the
    /// client config's <c>AuthenticationRegion</c> to <paramref name="region"/> too).
    /// </summary>
    /// <param name="region">The region, such as <c>eu-west-1</c> (an AWS SDK <c>RegionEndpoint</c>'s <c>SystemName</c>).</param>
    /// <param name="serviceType">The service.</param>
    /// <returns>The service URL.</returns>
    /// <exception cref="ArgumentException"><paramref name="region"/> isn't a region name: lowercase letters and digits in hyphen-separated parts.</exception>
    public static Uri GetServiceUrl(string region, AwsServiceType serviceType)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (!AwsRequestRegion.IsRegionName(region))
        {
            // The cloud reads a request's region from its endpoint host only when it looks like one, and would
            // otherwise serve it from CurrentRegion without a word.
            throw new ArgumentException($"'{region}' is not an AWS region name, such as 'eu-west-1'.", nameof(region));
        }

        return ServiceUrl(region, serviceType);
    }

    private static Uri ServiceUrl(string region, AwsServiceType serviceType) =>
        new($"https://{GetServiceName(serviceType)}.{region}.amazonaws.com");
}
