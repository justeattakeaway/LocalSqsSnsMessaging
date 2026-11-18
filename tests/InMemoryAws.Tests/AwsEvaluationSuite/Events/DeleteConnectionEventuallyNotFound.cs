// Translated from behavioral/events/check_delete_connection_eventually_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.delete-connection-eventually-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteConnectionEventuallyNotFound
{
    private static readonly string[] Settled = ["AUTHORIZED", "CREATE_FAILED"];

    /// <summary>
    /// DeleteConnection answers DELETING and is asynchronous: DescribeConnection converges on
    /// ResourceNotFoundException with its exact message, and a later UpdateConnection is not-found.
    /// </summary>
    [Test]
    [DisplayName("events.delete-connection-eventually-not-found")]
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
        ctx.OnCleanup(() => DeleteQuietly(ev, name));

        var clock = FidelityTime.StartNew();
        var settled = await ev.DescribeConnectionAsync(new DescribeConnectionRequest { Name = name });
        while (!Settled.Contains(settled.ConnectionState?.Value) && clock.Elapsed.TotalSeconds < 60)
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
            settled = await ev.DescribeConnectionAsync(new DescribeConnectionRequest { Name = name });
        }

        Ensure(
            Settled.Contains(settled.ConnectionState?.Value),
            $"connection must settle out of CREATING/AUTHORIZING before delete, last observed {settled.ConnectionState?.Value}");

        var deleted = await ev.DeleteConnectionAsync(new DeleteConnectionRequest { Name = name });
        Ensure(
            deleted.ConnectionState?.Value == "DELETING",
            $"DeleteConnection should report the connection entering DELETING, got {deleted.ConnectionState?.Value}");

        clock = FidelityTime.StartNew();
        var final = await Probe(ev, name);
        while (final.Item1 != "ResourceNotFoundException" && clock.Elapsed.TotalSeconds < 180)
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(5));
            final = await Probe(ev, name);
        }

        Ensure(
            final == ("ResourceNotFoundException", $"Failed to describe the connection(s). Connection '{name}' does not exist."),
            $"connection must eventually be gone, last observed {final}");

        var error = await FidelityContext.CaptureError(() =>
            ev.UpdateConnectionAsync(new UpdateConnectionRequest { Name = name, Description = "probe-after-delete" }));
        if (error is null)
        {
            throw Fail("UpdateConnection on the deleted connection's name SUCCEEDED");
        }

        (string?, string?) mutate = (error.Code, error.Message);
        Ensure(
            mutate == ("ResourceNotFoundException", $"Failed to update the connection(s). Connection '{name}' does not exist."),
            $"a mutation on the deleted connection's name must also be rejected as not-found, got {mutate}");
    }

    /// <summary>The connection's state, or the (code, message) of the DescribeConnection refusal.</summary>
    private static async Task<(string?, string?)> Probe(IAmazonEventBridge ev, string name)
    {
        var (response, error) = await FidelityContext.Capture(() => ev.DescribeConnectionAsync(new DescribeConnectionRequest { Name = name }));
        return error is null ? (response!.ConnectionState?.Value, "") : (error.Code, error.Message);
    }

    private static async Task DeleteQuietly(IAmazonEventBridge ev, string name)
    {
        var error = await FidelityContext.CaptureError(() => ev.DeleteConnectionAsync(new DeleteConnectionRequest { Name = name }));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }
}
