// Translated from behavioral/events/check_deauthorize_connection_state_transition.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.deauthorize-connection-state-transition</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeauthorizeConnectionStateTransition
{
    /// <summary>DeauthorizeConnection drives an AUTHORIZED connection to DEAUTHORIZED.</summary>
    [Test]
    [DisplayName("events.deauthorize-connection-state-transition")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var connName = FidelityContext.UniqueName("sim2real-behav-conn-");
        await ev.CreateConnectionAsync(new CreateConnectionRequest
        {
            Name = connName,
            AuthorizationType = ConnectionAuthorizationType.API_KEY,
            AuthParameters = new CreateConnectionAuthRequestParameters
            {
                ApiKeyAuthParameters = new CreateConnectionApiKeyAuthRequestParameters { ApiKeyName = "x-api-key", ApiKeyValue = "secret-value" },
            },
        });
        ctx.OnCleanup(() => CleanupConnection(ev, connName));

        // Both the AUTHORIZED and DEAUTHORIZED settles are asynchronous, hence the bounded waits.
        await WaitConnection(ev, connName, ["AUTHORIZED", "CREATE_FAILED"], 60);
        await ev.DeauthorizeConnectionAsync(new DeauthorizeConnectionRequest { Name = connName });
        var desc = await WaitConnection(ev, connName, ["DEAUTHORIZED"], 90);
        Ensure(
            desc.ConnectionState?.Value == "DEAUTHORIZED",
            $"expected DEAUTHORIZED, got {desc.ConnectionState?.Value}");
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

    private static async Task<DescribeConnectionResponse> WaitConnection(IAmazonEventBridge ev, string name, string[] states, double timeoutS)
    {
        var clock = FidelityTime.StartNew();
        var d = await ev.DescribeConnectionAsync(new DescribeConnectionRequest { Name = name });
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            d = await ev.DescribeConnectionAsync(new DescribeConnectionRequest { Name = name });
            if (states.Contains(d.ConnectionState?.Value))
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        return d;
    }
}
