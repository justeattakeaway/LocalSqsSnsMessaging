using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LocalSqsSnsMessaging.Tests.Verification;

public static class JetStackAspireExtensions
{
    public static IDistributedApplicationBuilder AddJetStack(this IDistributedApplicationBuilder builder)
    {
        var jetStackResource =
            builder
                .AddContainer("jet-stack", "ghcr.io/justeattakeaway/jet-stack", "latest")
                .WithHttpEndpoint(targetPort: 4566)
                .WithJetStackHealthCheck();

        var isRunningInCi = Environment.GetEnvironmentVariable("CI") == "true";

        if (!isRunningInCi)
        {
            jetStackResource
                .WithContainerName("localsqssnsmessaging-jetstack")
                .WithLifetime(ContainerLifetime.Persistent);
        }

        return builder;
    }

    private static IResourceBuilder<T> WithJetStackHealthCheck<T>(this IResourceBuilder<T> builder) where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);

        var endpoint = builder.Resource.GetEndpoint("http");
        if (endpoint.Scheme != "http")
        {
            throw new DistributedApplicationException($"Could not create HTTP health check for resource '{builder.Resource.Name}' as the endpoint with name '{endpoint.EndpointName}' and scheme '{endpoint.Scheme}' is not an HTTP endpoint.");
        }

        builder.EnsureEndpointIsAllocated(endpoint);

        Uri? baseUri = null;
        builder.ApplicationBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(builder.Resource, (@event, ct) =>
        {
            baseUri = new Uri(endpoint.Url, UriKind.Absolute);
            return Task.CompletedTask;
        });

        var healthCheckKey = $"{builder.Resource.Name}_jetstack_check";

        builder.ApplicationBuilder.Services.AddHealthChecks().Add(new HealthCheckRegistration(healthCheckKey,
            _ =>
            {
                return baseUri switch
                {
                    null => throw new DistributedApplicationException(
                        "The URI for the health check is not set. Ensure that the resource has been allocated before the health check is executed."),
                    _ => new JetStackHealthCheck(baseUri!)
                };
            }, failureStatus: null, tags: null));

        builder.WithHealthCheck(healthCheckKey);

        return builder;
    }

    private static void EnsureEndpointIsAllocated<T>(this IResourceBuilder<T> builder, EndpointReference endpoint)  where T : IResourceWithEndpoints
    {
        var endpointName = endpoint.EndpointName;

        builder.OnResourceEndpointsAllocated((_, _, _) =>
            endpoint.Exists switch
            {
                true => Task.CompletedTask,
                false => throw new DistributedApplicationException(
                    $"The endpoint '{endpointName}' does not exist on the resource '{builder.Resource.Name}'.")
            });
    }
}
