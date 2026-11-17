// Translated from behavioral/events/check_delete_connection_in_use_by_api_destination_allowed.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.delete-connection-in-use-by-api-destination-allowed</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteConnectionInUseByApiDestinationAllowed
{
    /// <summary>
    /// EventBridge does not reference-count connections: deleting one an API destination uses
    /// succeeds, and the destination stays ACTIVE with its now-dangling ConnectionArn.
    /// </summary>
    [Test]
    [DisplayName("events.delete-connection-in-use-by-api-destination-allowed")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var connName = FidelityContext.UniqueName("sim2real-behav-conn-");
        var conn = await ev.CreateConnectionAsync(new CreateConnectionRequest
        {
            Name = connName,
            AuthorizationType = ConnectionAuthorizationType.API_KEY,
            AuthParameters = new CreateConnectionAuthRequestParameters
            {
                ApiKeyAuthParameters = new CreateConnectionApiKeyAuthRequestParameters { ApiKeyName = "x-api-key", ApiKeyValue = "secret-value" },
            },
        });
        ctx.OnCleanup(() => CleanupConnection(ev, connName));
        await Wait(() => ev.DescribeConnectionAsync(new DescribeConnectionRequest { Name = connName }),
            d => d.ConnectionState?.Value is "AUTHORIZED" or "CREATE_FAILED", 60);

        var destName = FidelityContext.UniqueName("sim2real-behav-apidest-");
        await ev.CreateApiDestinationAsync(new CreateApiDestinationRequest
        {
            Name = destName,
            ConnectionArn = conn.ConnectionArn,
            InvocationEndpoint = "https://example.com/webhook",
            HttpMethod = ApiDestinationHttpMethod.POST,
        });
        ctx.OnCleanup(() => CleanupApiDestination(ev, destName));
        await Wait(() => ev.DescribeApiDestinationAsync(new DescribeApiDestinationRequest { Name = destName }),
            d => d.ApiDestinationState?.Value == "ACTIVE", 60);

        await ev.DeleteConnectionAsync(new DeleteConnectionRequest { Name = connName });
        var still = await ev.DescribeApiDestinationAsync(new DescribeApiDestinationRequest { Name = destName });
        Ensure(
            still.ConnectionArn == conn.ConnectionArn,
            $"API destination must keep its (now dangling) ConnectionArn: {still.ConnectionArn}");
        Ensure(
            still.ApiDestinationState?.Value == "ACTIVE",
            "API destination should stay ACTIVE even though its connection was just " +
            $"deleted from under it (real EventBridge does not revoke on dangling ref): {still.ApiDestinationState?.Value}");
    }

    /// <summary>Best-effort DeleteConnection (asynchronous; the connection disappears later).</summary>
    private static async Task CleanupConnection(IAmazonEventBridge ev, string name)
    {
        var error = await FidelityContext.CaptureError(() => ev.DeleteConnectionAsync(new DeleteConnectionRequest { Name = name }));
        if (error is not null && !(error.Code ?? "").Contains("NotFound", StringComparison.Ordinal))
        {
            throw error.Exception;
        }
    }

    private static async Task CleanupApiDestination(IAmazonEventBridge ev, string name)
    {
        var error = await FidelityContext.CaptureError(() => ev.DeleteApiDestinationAsync(new DeleteApiDestinationRequest { Name = name }));
        if (error is not null && !(error.Code ?? "").Contains("NotFound", StringComparison.Ordinal))
        {
            throw error.Exception;
        }
    }

    private static async Task<T> Wait<T>(Func<Task<T>> describe, Func<T, bool> predicate, double timeoutS)
    {
        var clock = FidelityTime.StartNew();
        var d = await describe();
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            d = await describe();
            if (predicate(d))
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        return d;
    }
}
