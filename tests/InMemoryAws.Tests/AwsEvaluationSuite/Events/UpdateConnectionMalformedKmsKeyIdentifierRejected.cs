// Translated from behavioral/events/check_update_connection_malformed_kms_key_identifier_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.update-connection-malformed-kms-key-identifier-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateConnectionMalformedKmsKeyIdentifierRejected
{
    private static readonly string[] Settled = ["AUTHORIZED", "CREATE_FAILED"];

    /// <summary>
    /// UpdateConnection refuses a KmsKeyIdentifier that does not match its documented pattern,
    /// leaving the connection's description unchanged, while a plain description update succeeds.
    /// </summary>
    [Test]
    [DisplayName("events.update-connection-malformed-kms-key-identifier-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var name = FidelityContext.UniqueName("sim2real-behav-conn-");
        await events.CreateConnectionAsync(new CreateConnectionRequest
        {
            Name = name,
            Description = "sim2real original description",
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

        var bogusKey = "!!!not-a-kms-identifier!!!"; // a variable: the SDK's analyzer refuses the literal
        var (code, message) = await Error(() => events.UpdateConnectionAsync(new UpdateConnectionRequest { Name = name, KmsKeyIdentifier = bogusKey }));
        Ensure(
            (code, message) == (
                "ValidationException",
                $"1 validation error detected: Value '{bogusKey}' at 'kmsKeyIdentifier' failed to satisfy " +
                @"constraint: Member must satisfy regular expression pattern: ^[a-zA-Z0-9_\-/:]*$"),
            $"the malformed KmsKeyIdentifier must be refused exactly, got '{code}' '{message}'");

        var unaffected = await Describe(events, name);
        Ensure(
            unaffected.Description == "sim2real original description",
            $"the refused update must leave the description unchanged, got {unaffected.Description}");

        var updated = await events.UpdateConnectionAsync(new UpdateConnectionRequest { Name = name, Description = "sim2real updated description" });
        Ensure(
            updated.ConnectionState?.Value == "AUTHORIZED",
            $"a plain description update must leave an authorized connection authorized, got {updated.ConnectionState?.Value}");

        var final = await Describe(events, name);
        Ensure(
            final.Description == "sim2real updated description",
            $"the accepted update must change the description, got {final.Description}");
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
