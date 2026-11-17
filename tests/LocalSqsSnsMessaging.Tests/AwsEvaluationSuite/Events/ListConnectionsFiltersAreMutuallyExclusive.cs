// Translated from behavioral/events/check_list_connections_filters_are_mutually_exclusive.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.list-connections-filters-are-mutually-exclusive</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListConnectionsFiltersAreMutuallyExclusive
{
    private const string CombinedFilterRefusal =
        "Failed to list the connection(s). Request failed because only one filter is supported per " +
        "request, either ConnectionState or NamePrefix.";

    /// <summary>
    /// ListConnections accepts one filter per request: NamePrefix and ConnectionState each work
    /// alone, but combining them is rejected with ValidationException.
    /// </summary>
    [Test]
    [DisplayName("events.list-connections-filters-are-mutually-exclusive")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var connName = FidelityContext.UniqueName("sim2real-behav-conn-");
        var name = connName;
        await ev.CreateConnectionAsync(new CreateConnectionRequest
        {
            Name = connName,
            AuthorizationType = ConnectionAuthorizationType.API_KEY,
            AuthParameters = new CreateConnectionAuthRequestParameters
            {
                ApiKeyAuthParameters = new CreateConnectionApiKeyAuthRequestParameters { ApiKeyName = "x-api-key", ApiKeyValue = "secret-value" },
            },
        });
        ctx.OnCleanup(() => CleanupConnection(ev, name));

        // The connection reaches AUTHORIZED asynchronously.
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < 60)
        {
            var d = await ev.DescribeConnectionAsync(new DescribeConnectionRequest { Name = name });
            if (d.ConnectionState?.Value is "AUTHORIZED" or "CREATE_FAILED")
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        var byPrefix = (await ev.ListConnectionsAsync(new ListConnectionsRequest { NamePrefix = name })).Connections ?? [];
        Ensure(
            byPrefix.Select(c => c.Name).ToHashSet().SetEquals([name]),
            $"NamePrefix filter broken: [{string.Join(", ", byPrefix.Select(c => c.Name))}]");
        Ensure(byPrefix[0].ConnectionState?.Value == "AUTHORIZED", $"unexpected state: {byPrefix[0].ConnectionState?.Value}");

        var err = await Error(() => ev.ListConnectionsAsync(new ListConnectionsRequest { NamePrefix = name, ConnectionState = ConnectionState.AUTHORIZED }));
        Ensure(
            err == ("ValidationException", CombinedFilterRefusal),
            $"expected ValidationException when combining ListConnections filters, got {err}");

        var deauthorized = (await ev.ListConnectionsAsync(new ListConnectionsRequest { ConnectionState = ConnectionState.DEAUTHORIZED })).Connections ?? [];
        Ensure(
            !deauthorized.Any(c => c.Name == name),
            $"AUTHORIZED connection must not match DEAUTHORIZED filter: [{string.Join(", ", deauthorized.Select(c => c.Name))}]");
    }

    /// <summary>(code, message) of a call that must fail; a call that succeeds raises AssertionError.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected a ClientError, but the call succeeded");
        }

        return (error.Code, error.Message);
    }

    /// <summary>Best-effort DeleteConnection (asynchronous; the connection disappears later).</summary>
    private static async Task CleanupConnection(IAmazonEventBridge ev, string name)
    {
        var error = await FidelityContext.CaptureError(() => ev.DeleteConnectionAsync(new DeleteConnectionRequest { Name = name }));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }
}
