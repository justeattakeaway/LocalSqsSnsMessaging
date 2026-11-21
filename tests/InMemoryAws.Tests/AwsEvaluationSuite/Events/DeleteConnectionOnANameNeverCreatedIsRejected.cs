// Translated from behavioral/events/check_delete_connection_on_a_name_never_created_is_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.delete-connection-on-a-name-never-created-is-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteConnectionOnANameNeverCreatedIsRejected
{
    private static readonly string[] Settled = ["AUTHORIZED", "CREATE_FAILED"];

    /// <summary>
    /// DeleteConnection on a name that was never created is refused as not-found, with a real
    /// connection unaffected, and the same call against that connection succeeds.
    /// </summary>
    [Test]
    [DisplayName("events.delete-connection-on-a-name-never-created-is-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var name = FidelityContext.UniqueName("sim2real-behav-conn-");
        var created = await events.CreateConnectionAsync(new CreateConnectionRequest
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
        var (code, message) = await Error(() => events.DeleteConnectionAsync(new DeleteConnectionRequest { Name = missing }));
        Ensure(
            (code, message) == ("ResourceNotFoundException", $"Failed to delete the connection(s). Connection '{missing}' does not exist."),
            $"deleting a name that was never created must be not-found, got '{code}' '{message}'");

        var unaffected = await Describe(events, name);
        Ensure(
            unaffected.ConnectionState?.Value == "AUTHORIZED",
            $"the refused call on a different name must not touch the real connection, got {unaffected.ConnectionState?.Value}");

        var deleted = await events.DeleteConnectionAsync(new DeleteConnectionRequest { Name = name });
        Ensure(
            deleted.ConnectionArn == created.ConnectionArn,
            $"deleting the real connection must act on the ARN it was created with, got {deleted.ConnectionArn}");
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
