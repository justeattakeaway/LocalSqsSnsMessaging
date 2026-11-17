// Translated from behavioral/events/check_api_destination_state_follows_connection_deauthorize_and_reauthorize.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.api-destination-state-follows-connection-deauthorize-and-reauthorize</summary>
[Category("AwsEvaluationSuite")]
public sealed class ApiDestinationStateFollowsConnectionDeauthorizeAndReauthorize
{
    private const double SettleTimeoutS = 180.0;
    private const string HookUrl = "https://sim2real.invalid/hook";

    /// <summary>
    /// An API destination's state follows its BASIC connection: ACTIVE while AUTHORIZED, INACTIVE
    /// after DeauthorizeConnection, ACTIVE again after re-authorizing; an UpdateConnection naming auth
    /// parameters without AuthorizationType is refused and leaves the connection deauthorized.
    /// </summary>
    [Test]
    [DisplayName("events.api-destination-state-follows-connection-deauthorize-and-reauthorize")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var conn = FidelityContext.UniqueName("sim2real-behav-conn-");
        var connArn = (await events.CreateConnectionAsync(new CreateConnectionRequest
        {
            Name = conn,
            AuthorizationType = ConnectionAuthorizationType.BASIC,
            AuthParameters = new CreateConnectionAuthRequestParameters
            {
                BasicAuthParameters = new CreateConnectionBasicAuthRequestParameters { Username = "sim2real", Password = "sim2real-pw-1" },
            },
        })).ConnectionArn;
        ctx.OnCleanup(() => events.DeleteConnectionAsync(new DeleteConnectionRequest { Name = conn }));
        await SettleConnection(events, conn, "AUTHORIZED");

        var dest = FidelityContext.UniqueName("sim2real-behav-dest-");
        var destArn = (await events.CreateApiDestinationAsync(new CreateApiDestinationRequest
        {
            Name = dest, ConnectionArn = connArn, InvocationEndpoint = HookUrl, HttpMethod = ApiDestinationHttpMethod.POST,
        })).ApiDestinationArn;
        ctx.OnCleanup(() => events.DeleteApiDestinationAsync(new DeleteApiDestinationRequest { Name = dest }));
        await SettleDestination(events, dest, "ACTIVE");

        await events.DeauthorizeConnectionAsync(new DeauthorizeConnectionRequest { Name = conn });
        await SettleConnection(events, conn, "DEAUTHORIZED");
        await SettleDestination(events, dest, "INACTIVE");

        var (code, message) = await Error(() => events.UpdateConnectionAsync(new UpdateConnectionRequest
        {
            Name = conn,
            AuthParameters = new UpdateConnectionAuthRequestParameters
            {
                BasicAuthParameters = new UpdateConnectionBasicAuthRequestParameters { Username = "sim2real", Password = "sim2real-pw-2" },
            },
        }));
        Ensure(
            (code, message) == (
                "ValidationException",
                "Failed to update the connection(s). At least one of the following is required: Description, " +
                "KmsKeyIdentifier, AuthorizationType with BasicAuthParameters, ApiKeyAuthParameters, or OAuthParameters."),
            $"auth parameters without AuthorizationType must be refused, got '{code}' '{message}'");
        Ensure(
            (await events.DescribeConnectionAsync(new DescribeConnectionRequest { Name = conn })).ConnectionState?.Value == "DEAUTHORIZED",
            "the refused UpdateConnection must leave the connection DEAUTHORIZED");

        await events.UpdateConnectionAsync(new UpdateConnectionRequest
        {
            Name = conn,
            AuthorizationType = ConnectionAuthorizationType.BASIC,
            AuthParameters = new UpdateConnectionAuthRequestParameters
            {
                BasicAuthParameters = new UpdateConnectionBasicAuthRequestParameters { Username = "sim2real", Password = "sim2real-pw-2" },
            },
        });
        await SettleConnection(events, conn, "AUTHORIZED");
        await SettleDestination(events, dest, "ACTIVE");
        var described = await events.DescribeApiDestinationAsync(new DescribeApiDestinationRequest { Name = dest });
        Ensure(
            described.ApiDestinationArn == destArn && described.ConnectionArn == connArn,
            "re-authorizing must keep the same API destination bound to the same connection");
    }

    private static async Task SettleConnection(IAmazonEventBridge events, string name, string want)
    {
        string[] transient = ["CREATING", "UPDATING", "AUTHORIZING", "DEAUTHORIZING"];
        var got = await Poll(() => events.DescribeConnectionAsync(new DescribeConnectionRequest { Name = name }),
            d => !transient.Contains(d.ConnectionState?.Value), "connection leaves ('CREATING', 'UPDATING', 'AUTHORIZING', 'DEAUTHORIZING')");
        Ensure(
            got.ConnectionState?.Value == want,
            $"connection settled in '{got.ConnectionState?.Value}' ('{got.StateReason}'), expected '{want}'");
    }

    private static async Task SettleDestination(IAmazonEventBridge events, string name, string want)
    {
        var got = await Poll(async () => (await events.DescribeApiDestinationAsync(new DescribeApiDestinationRequest { Name = name })).ApiDestinationState?.Value,
            s => s == want, $"API destination becomes {want}");
        Ensure(got == want, $"API destination state '{got}', expected '{want}'");
    }

    private static async Task<T> Poll<T>(Func<Task<T>> read, Func<T, bool> done, string what)
    {
        var clock = FidelityTime.StartNew();
        T? value = default;
        while (clock.Elapsed.TotalSeconds < SettleTimeoutS)
        {
            value = await read();
            if (done(value))
            {
                return value;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"timed out after {SettleTimeoutS}s waiting until {what}; last read {value}");
    }

    /// <summary>(code, message) of a call that must fail; an unexpected success raises, naming the result.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected the call to be refused, but it succeeded");
        }

        return (error.Code, error.Message);
    }
}
