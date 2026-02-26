using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LocalSqsSnsMessaging.Tests.Verification;

#pragma warning disable CA1001
public sealed class MotoServerHealthCheck(Uri uri) : IHealthCheck
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
            using var response = await _client.GetAsync("moto-api/", cancellationToken);
#pragma warning restore CA2234
            if (response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Healthy("Moto Server is healthy.");
            }

            return HealthCheckResult.Unhealthy("Moto Server is unhealthy.");
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (Exception ex)
#pragma warning restore CA1031 // Do not catch general exception types
        {
            return HealthCheckResult.Unhealthy("Moto Server is unhealthy.", ex);
        }
    }
}
