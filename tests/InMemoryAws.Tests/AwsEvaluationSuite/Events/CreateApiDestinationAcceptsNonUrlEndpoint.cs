// Translated from behavioral/events/check_create_api_destination_accepts_non_url_endpoint.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.create-api-destination-accepts-non-url-endpoint</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateApiDestinationAcceptsNonUrlEndpoint
{
    /// <summary>
    /// EventBridge does not validate that InvocationEndpoint is a URL: CreateApiDestination with a
    /// bare string is accepted, becomes ACTIVE, and stores the endpoint verbatim.
    /// </summary>
    [Test]
    [DisplayName("events.create-api-destination-accepts-non-url-endpoint")]
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
        var created = await ev.CreateApiDestinationAsync(new CreateApiDestinationRequest
        {
            Name = destName,
            ConnectionArn = conn.ConnectionArn,
            InvocationEndpoint = "not-a-url",
            HttpMethod = ApiDestinationHttpMethod.POST,
        });
        ctx.OnCleanup(() => CleanupApiDestination(ev, destName));
        Ensure(created.ApiDestinationState?.Value is "ACTIVE" or "INACTIVE", $"unexpected state: {created.ApiDestinationState?.Value}");
        await Wait(() => ev.DescribeApiDestinationAsync(new DescribeApiDestinationRequest { Name = destName }),
            d => d.ApiDestinationState?.Value == "ACTIVE", 60);
        var desc = await ev.DescribeApiDestinationAsync(new DescribeApiDestinationRequest { Name = destName });
        Ensure(
            desc.InvocationEndpoint == "not-a-url",
            $"endpoint must be stored verbatim, got {desc.InvocationEndpoint}");
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
                return d;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        throw new TimeoutException($"condition not met within {timeoutS}s: last={d}");
    }
}
