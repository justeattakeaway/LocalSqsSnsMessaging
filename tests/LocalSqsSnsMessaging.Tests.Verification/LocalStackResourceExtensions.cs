using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LocalSqsSnsMessaging.Tests.Verification;

public static class LocalStackResourceExtensions
{
    public static IResourceBuilder<T> WithLocalStackHealthCheck<T>(this IResourceBuilder<T> builder, string[] services) where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);

        var endpoint = builder.Resource.GetEndpoint("http");
        if (endpoint.Scheme != "http")
        {
            throw new DistributedApplicationException($"Could not create HTTP health check for resource '{builder.Resource.Name}' as the endpoint with name '{endpoint.EndpointName}' and scheme '{endpoint.Scheme}' is not an HTTP endpoint.");
        }

        var endpointName = endpoint.EndpointName;

        builder.ApplicationBuilder.Eventing.Subscribe<AfterEndpointsAllocatedEvent>((@event, ct) =>
        {
            if (!endpoint.Exists)
            {
                throw new DistributedApplicationException($"The endpoint '{endpointName}' does not exist on the resource '{builder.Resource.Name}'.");
            }

            return Task.CompletedTask;
        });

        Uri? baseUri = null;
        builder.ApplicationBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(builder.Resource, (@event, ct) =>
        {
            baseUri = new Uri(endpoint.Url, UriKind.Absolute);
            return Task.CompletedTask;
        });

        var healthCheckKey = $"{builder.Resource.Name}_localstack_check";

        builder.ApplicationBuilder.Services.AddHealthChecks().Add(new HealthCheckRegistration(healthCheckKey,
            _ =>
            {
                return baseUri switch
                {
                    null => throw new DistributedApplicationException(
                        "The URI for the health check is not set. Ensure that the resource has been allocated before the health check is executed."),
                    _ => new LocalStackHealthCheck(baseUri!, services)
                };
            }, failureStatus: null, tags: null));

        builder.WithHealthCheck(healthCheckKey);

        return builder;
    }
}
