// Translated from behavioral/events/check_deauthorize_connection_on_a_name_never_created_is_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.deauthorize-connection-on-a-name-never-created-is-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeauthorizeConnectionOnANameNeverCreatedIsRejected
{
    private static readonly string[] Settled = ["AUTHORIZED", "CREATE_FAILED"];

    /// <summary>
    /// DeauthorizeConnection on a name that was never created is refused as not-found, with the
    /// real connection unaffected, and the same call against it succeeds.
    /// </summary>
    [Test]
    [DisplayName("events.deauthorize-connection-on-a-name-never-created-is-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var name = FidelityContext.UniqueName("sim2real-behav-conn-");
        await events.CreateConnectionAsync(new CreateConnectionRequest
        {
            Name = name,
            AuthorizationType = ConnectionAuthorizationType.API_KEY,
            AuthParameters = new CreateConnectionAuthRequestParameters
            {
                ApiKeyAuthParameters = new CreateConnectionApiKeyAuthRequestParameters { ApiKeyName = "x-api-key", ApiKeyValue = "secret-value" },
            },
        });
        ctx.OnCleanup(() => events.DeleteConnectionAsync(new DeleteConnectionRequest { Name = name }));

        var deadline = FidelityTime.StartNew();
        var settled = await Describe(events, name);
        while (!Settled.Contains(settled.ConnectionState?.Value) && deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
            settled = await Describe(events, name);
        }

        Ensure(
            settled.ConnectionState?.Value == "AUTHORIZED",
            $"the connection must settle AUTHORIZED before the probe, last observed {settled.ConnectionState?.Value}");

        var missing = FidelityContext.UniqueName("sim2real-behav-conn-");
        var (code, message) = await Error(() => events.DeauthorizeConnectionAsync(new DeauthorizeConnectionRequest { Name = missing }));
        Ensure(
            (code, message) == ("ResourceNotFoundException", $"Failed to deauthorize the connection(s). Connection '{missing}' does not exist."),
            $"deauthorizing a name that was never created must be not-found, got '{code}' '{message}'");

        var unaffected = await Describe(events, name);
        Ensure(
            unaffected.ConnectionState?.Value == "AUTHORIZED",
            $"the refused call on a different name must not touch the real connection, got {unaffected.ConnectionState?.Value}");

        var control = await events.DeauthorizeConnectionAsync(new DeauthorizeConnectionRequest { Name = name });
        Ensure(
            control.ConnectionState?.Value == "DEAUTHORIZING",
            $"deauthorizing the real connection must start the transition, got {control.ConnectionState?.Value}");

        deadline = FidelityTime.StartNew();
        var final = await Describe(events, name);
        while (final.ConnectionState?.Value != "DEAUTHORIZED" && deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
            final = await Describe(events, name);
        }

        Ensure(
            final.ConnectionState?.Value == "DEAUTHORIZED",
            $"the real connection must settle DEAUTHORIZED, last observed {final.ConnectionState?.Value}");
    }

    private static Task<DescribeConnectionResponse> Describe(IAmazonEventBridge events, string name) =>
        events.DescribeConnectionAsync(new DescribeConnectionRequest { Name = name });

    /// <summary>(code, message) of a call that must fail; an unexpected success raises, naming the result.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }
}
