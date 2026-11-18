using InMemoryAws.EventBridge.Model;

namespace InMemoryAws;

// Delivery to API destinations: an HTTP call per matched event, made in the background so PutEvents
// never waits on an endpoint.
internal static partial class EventBridgeEventDelivery
{
    /// <summary>The retry policy a target without one gets: 185 retries, for up to a day.</summary>
    private const int DefaultMaximumRetryAttempts = 185;
    private const int DefaultMaximumEventAgeInSeconds = 86_400;

    /// <summary>The longest EventBridge waits between two attempts.</summary>
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Calls the API destination <paramref name="destinationArn"/> names with <paramref name="body"/>,
    /// in the background. Each attempt waits for a slot under the destination's rate limit (so a burst
    /// is deferred, never dropped), then sends <see cref="EventBridgeHttp.InvocationRequest"/>.
    /// </summary>
    /// <remarks>
    /// A 2xx answer delivers the event. A 401, 407, 409, 429 or 5xx answer, a timeout or a failed
    /// connection is retried with exponential backoff, on the cloud's clock, until the target's
    /// <c>RetryPolicy</c> runs out; any other answer fails at once. A failed event goes to the target's
    /// dead-letter queue: <c>INVALID_PARAMETER</c> for an answer that is not retried, else with
    /// <c>EXHAUSTED_RETRY_CONDITION</c> and <c>RETRY_ATTEMPTS</c>.
    /// </remarks>
    private static void InvokeApiDestination(Delivery delivery, Arn destinationArn, string body, InMemoryAwsCloud cloud) =>
        _ = InvokeApiDestinationAsync(delivery, destinationArn, body, cloud);

    private static async Task InvokeApiDestinationAsync(Delivery delivery, Arn destinationArn, string body, InMemoryAwsCloud cloud)
    {
        try
        {
            var maxRetries = delivery.Target.RetryPolicy?.MaximumRetryAttempts ?? DefaultMaximumRetryAttempts;
            var maxAge = TimeSpan.FromSeconds(delivery.Target.RetryPolicy?.MaximumEventAgeInSeconds ?? DefaultMaximumEventAgeInSeconds);
            for (var retries = 0; ; retries++)
            {
                var (failure, retryable) = await InvokeOnceAsync(delivery, destinationArn, body, cloud).ConfigureAwait(false);
                if (failure is null)
                {
                    return;
                }

                if (!retryable)
                {
                    DeadLetter(delivery, body, failure, cloud);
                    return;
                }

                if (retries >= maxRetries)
                {
                    DeadLetter(delivery, body, failure with { ExhaustedRetryCondition = "MaximumRetryAttempts", RetryAttempts = retries }, cloud);
                    return;
                }

                var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, retries), MaxRetryDelay.TotalSeconds));
                if (cloud.TimeProvider.GetUtcNow() + delay - delivery.Event.IngestionTime > maxAge)
                {
                    DeadLetter(delivery, body, failure with { ExhaustedRetryCondition = "MaximumEventAgeInSeconds", RetryAttempts = retries }, cloud);
                    return;
                }

                await cloud.TimeProvider.Delay(delay).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // Background delivery must never surface an exception to the publisher.
        catch (Exception)
        {
            // Swallowed intentionally - see pragma above.
        }
#pragma warning restore CA1031
    }

    /// <summary>One attempt: no failure when the endpoint accepted the event, else why not and whether to retry.</summary>
    private static async Task<(DeliveryFailure? Failure, bool Retryable)> InvokeOnceAsync(Delivery delivery, Arn destinationArn, string body, InMemoryAwsCloud cloud)
    {
        if (!TryGetApiDestination(cloud, destinationArn, out var destination))
        {
            var name = destinationArn.Resource.Split('/') is { Length: > 1 } parts ? parts[1] : destinationArn.Resource;
            return (new DeliveryFailure("NO_RESOURCE", $"An api-destination '{name}' does not exist."), false);
        }

        var wait = destination.TakeInvocationSlot(cloud.TimeProvider.GetUtcNow());
        if (wait > TimeSpan.Zero)
        {
            await cloud.TimeProvider.Delay(wait).ConfigureAwait(false);
        }

        var settings = destination.Settings;
        var connection = TryGetConnection(cloud, settings.ConnectionArn, out var found) ? found.Snapshot() : null;
        if (connection is not null && connection.State != ConnectionResource.Authorized)
        {
            return (new DeliveryFailure("INVALID_PARAMETER", $"The connection {connection.Arn} is not authorized."), false);
        }

        using var request = EventBridgeHttp.InvocationRequest(settings, connection, delivery.Target, body);
        if (request is null)
        {
            return (new DeliveryFailure("INVALID_PARAMETER", $"The api-destination endpoint '{settings.InvocationEndpoint}' is not a valid URL."), false);
        }

        var response = await EventBridgeHttp.SendAsync(cloud, request).ConfigureAwait(false);
        if (response.StatusCode is not { } status)
        {
            return (new DeliveryFailure("SDK_CLIENT_ERROR", response.TimedOut
                ? "Unable to invoke ApiDestination endpoint: The request timed out."
                : "Unable to invoke ApiDestination endpoint: Could not connect to the endpoint."), true);
        }

        if (response.Succeeded)
        {
            return (null, false);
        }

        var message = $"ApiDestination returned HTTP status {status} with payload: {response.Payload}";
        return status is 401 or 407 or 409 or 429 or >= 500
            ? (new DeliveryFailure("UNKNOWN", message), true)
            : (new DeliveryFailure("INVALID_PARAMETER", message), false);
    }

    /// <summary>The API destination with this ARN, in the region the ARN names.</summary>
    internal static bool TryGetApiDestination(InMemoryAwsCloud cloud, Arn arn, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ApiDestinationResource? destination)
    {
        destination = null;
        var parts = arn.Resource.Split('/');
        return parts.Length == 3
            && string.Equals(arn.Account, cloud.CurrentAccountId, StringComparison.Ordinal)
            && cloud.TryGetRegion(arn.Region, out var region)
            && region.EventBridgeApiDestinations.TryGetValue(parts[1], out destination)
            && string.Equals(destination.Arn, arn.Value, StringComparison.Ordinal);
    }

    /// <summary>The connection with this ARN, in the region the ARN names.</summary>
    internal static bool TryGetConnection(InMemoryAwsCloud cloud, string? connectionArn, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ConnectionResource? connection)
    {
        connection = null;
        if (!Arn.TryParse(connectionArn, out var arn))
        {
            return false;
        }

        var parts = arn.Resource.Split('/');
        return parts.Length == 3
            && string.Equals(parts[0], "connection", StringComparison.Ordinal)
            && string.Equals(arn.Account, cloud.CurrentAccountId, StringComparison.Ordinal)
            && cloud.TryGetRegion(arn.Region, out var region)
            && region.EventBridgeConnections.TryGetValue(parts[1], out connection)
            && string.Equals(connection.Arn, arn.Value, StringComparison.Ordinal);
    }
}
