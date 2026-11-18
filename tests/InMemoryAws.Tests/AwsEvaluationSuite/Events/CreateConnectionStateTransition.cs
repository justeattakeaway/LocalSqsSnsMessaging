// Translated from behavioral/events/check_create_connection_state_transition.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.create-connection-state-transition</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateConnectionStateTransition
{
    /// <summary>
    /// CreateConnection settles into AUTHORIZED after the transient CREATING/AUTHORIZING states,
    /// and DescribeConnection reflects it.
    /// </summary>
    [Test]
    [DisplayName("events.create-connection-state-transition")]
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
        var desc = await ev.DescribeConnectionAsync(new DescribeConnectionRequest { Name = connName });
        Ensure(
            desc.ConnectionState?.Value is "CREATING" or "AUTHORIZING" or "AUTHORIZED",
            $"unexpected initial ConnectionState: {desc.ConnectionState?.Value}");

        // The AUTHORIZED settle is asynchronous, so poll to a bounded deadline.
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < 60)
        {
            desc = await ev.DescribeConnectionAsync(new DescribeConnectionRequest { Name = connName });
            if (desc.ConnectionState?.Value is "AUTHORIZED" or "CREATE_FAILED")
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        Ensure(
            desc.ConnectionState?.Value == "AUTHORIZED",
            $"expected AUTHORIZED, got {desc.ConnectionState?.Value} ({desc.StateReason})");
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
}
