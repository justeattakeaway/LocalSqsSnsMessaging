using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LocalSqsSnsMessaging.Tests.Verification;

#pragma warning disable CA1001
public sealed class JetStackHealthCheck(Uri uri) : IHealthCheck
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
            using var response = await _client.GetAsync("_jetstack/health", cancellationToken);
#pragma warning restore CA2234
            if (response.IsSuccessStatusCode)
            {
                var responseJson =
                    await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: cancellationToken);
                var status = responseJson?["status"]?.ToString();

                if (status == "ok")
                {
                    return HealthCheckResult.Healthy("jet-stack is healthy.");
                }

                return HealthCheckResult.Unhealthy(
                    $"jet-stack health check returned unexpected status: {status}"
                );
            }

            return HealthCheckResult.Unhealthy("jet-stack is unhealthy.");
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
        {
            return HealthCheckResult.Unhealthy("jet-stack is unhealthy.", ex);
        }
    }
}
