using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LocalSqsSnsMessaging.Tests.Verification;

#pragma warning disable CA1001
public sealed class LocalStackHealthCheck(Uri uri, string[] services) : IHealthCheck
{
    private readonly HttpClient _client =
        new(new SocketsHttpHandler { ActivityHeadersPropagator = null })
        {
            BaseAddress = uri, Timeout = TimeSpan.FromSeconds(1)
        };

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
#pragma warning disable CA2234
            using var response = await _client.GetAsync("_localstack/health", cancellationToken);
#pragma warning restore CA2234
            if (response.IsSuccessStatusCode)
            {
                var responseJson =
                    await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: cancellationToken);
                var servicesNode = responseJson?["services"]?.AsObject();

                if (servicesNode is null)
                {
                    return HealthCheckResult.Unhealthy(
                        "LocalStack health response did not contain a 'services' object."
                    );
                }

                var failingServices = services
                    .Where(s =>
                        !servicesNode.ContainsKey(s)
                        || servicesNode[s]?.ToString() != "running"
                    )
                    .ToList();

                if (failingServices.Count == 0)
                {
                    return HealthCheckResult.Healthy("LocalStack is healthy.");
                }

                var reason =
                    $"The following required services are not running: {string.Join(", ", failingServices)}.";
                return HealthCheckResult.Unhealthy(
                    $"LocalStack is unhealthy. {reason}"
                );
            }

            return HealthCheckResult.Unhealthy("LocalStack is unhealthy. SNS or SQS is not running.");
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
        {
            return HealthCheckResult.Unhealthy("LocalStack is unhealthy.", ex);
        }
    }
}
