// Translated from behavioral/events/check_update_connection_authorization_type.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.update-connection-authorization-type</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateConnectionAuthorizationType
{
    /// <summary>
    /// UpdateConnection switches AuthorizationType from API_KEY to BASIC in place; the connection
    /// returns to AUTHORIZED with the same ConnectionArn.
    /// </summary>
    [Test]
    [DisplayName("events.update-connection-authorization-type")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var connName = FidelityContext.UniqueName("sim2real-behav-conn-");
        var created = await ev.CreateConnectionAsync(new CreateConnectionRequest
        {
            Name = connName,
            AuthorizationType = ConnectionAuthorizationType.API_KEY,
            AuthParameters = new CreateConnectionAuthRequestParameters
            {
                ApiKeyAuthParameters = new CreateConnectionApiKeyAuthRequestParameters { ApiKeyName = "x-api-key", ApiKeyValue = "secret-value" },
            },
        });
        ctx.OnCleanup(() => CleanupConnection(ev, connName));

        // Connection authorization settles asynchronously.
        await WaitConnection(ev, connName, d => d.ConnectionState?.Value is "AUTHORIZED" or "CREATE_FAILED", 60);
        await ev.UpdateConnectionAsync(new UpdateConnectionRequest
        {
            Name = connName,
            AuthorizationType = ConnectionAuthorizationType.BASIC,
            AuthParameters = new UpdateConnectionAuthRequestParameters
            {
                BasicAuthParameters = new UpdateConnectionBasicAuthRequestParameters { Username = "user1", Password = "pass-1234" },
            },
        });
        var desc = await WaitConnection(
            ev,
            connName,
            d => d.ConnectionState?.Value is "AUTHORIZED" or "UPDATE_FAILED"
                 && d.AuthorizationType?.Value == "BASIC",
            90);
        Ensure(desc.ConnectionState?.Value == "AUTHORIZED", $"expected AUTHORIZED, got {desc.ConnectionState?.Value}");
        Ensure(desc.AuthorizationType?.Value == "BASIC", $"auth type not updated: {desc.AuthorizationType?.Value}");
        Ensure(desc.ConnectionArn == created.ConnectionArn, "ConnectionArn must be stable");
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

    private static async Task<DescribeConnectionResponse> WaitConnection(IAmazonEventBridge ev, string name, Func<DescribeConnectionResponse, bool> predicate, double timeoutS)
    {
        var clock = FidelityTime.StartNew();
        var d = await ev.DescribeConnectionAsync(new DescribeConnectionRequest { Name = name });
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            d = await ev.DescribeConnectionAsync(new DescribeConnectionRequest { Name = name });
            if (predicate(d))
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        return d;
    }
}
