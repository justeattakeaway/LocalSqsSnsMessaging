namespace LocalSqsSnsMessaging.Tests.Verification;

public static class FlociAspireExtensions
{
    // Pinned for reproducible CI/local runs. Renovate keeps this current via the
    // custom manager in renovate.json (see the marker comment below).
    // renovate: datasource=docker depName=floci/floci
    private const string FlociImageTag = "1.5.32";

    public static IDistributedApplicationBuilder AddFloci(this IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var flociResource =
            builder
                .AddContainer("floci", "floci/floci", FlociImageTag)
                .WithHttpEndpoint(targetPort: 4566)
                // floci exposes a LocalStack-style readiness endpoint. Use Aspire's built-in
                // HTTP health check, which defers reading the allocated endpoint until the
                // check actually runs. The previous custom check read endpoint.Url eagerly in
                // BeforeResourceStartedEvent, which is not yet allocated for persistent-lifetime
                // containers, causing the resource to fail to start on local runs.
                .WithHttpHealthCheck("/_floci/health");

        var isRunningInCi = Environment.GetEnvironmentVariable("CI") == "true";

        if (!isRunningInCi)
        {
            flociResource
                .WithContainerName("localsqssnsmessaging-floci")
                .WithLifetime(ContainerLifetime.Persistent);
        }

        return builder;
    }
}
