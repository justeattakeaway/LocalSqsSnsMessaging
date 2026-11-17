// Translated from behavioral/events/check_create_api_destination_requires_existing_connection.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.create-api-destination-requires-existing-connection</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateApiDestinationRequiresExistingConnection
{
    /// <summary>
    /// CreateApiDestination with a ConnectionArn naming no existing connection fails with
    /// ResourceNotFoundException and creates nothing; the call succeeds against a real connection.
    /// </summary>
    [Test]
    [DisplayName("events.create-api-destination-requires-existing-connection")]
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

        // Positive control: CreateApiDestination against a real connection succeeds.
        var goodName = FidelityContext.UniqueName("sim2real-behav-apidest-good-");
        var created = await ev.CreateApiDestinationAsync(new CreateApiDestinationRequest
        {
            Name = goodName,
            ConnectionArn = conn.ConnectionArn,
            InvocationEndpoint = "https://example.com/webhook",
            HttpMethod = ApiDestinationHttpMethod.POST,
        });
        ctx.OnCleanup(() => CleanupApiDestination(ev, goodName));
        Ensure(created.ApiDestinationState?.Value is "ACTIVE" or "INACTIVE", $"unexpected state: {created.ApiDestinationState?.Value}");

        // Build a well-formed but non-existent connection ARN in THIS account/region
        // so the failure is genuinely "not found" rather than a cross-account issue.
        var bogusConnArn = conn.ConnectionArn[..conn.ConnectionArn.LastIndexOf(":connection/", StringComparison.Ordinal)] +
            $":connection/{FidelityContext.UniqueName("sim2real-behav-no-conn-")}/" +
            "00000000-0000-0000-0000-000000000000";
        var badName = FidelityContext.UniqueName("sim2real-behav-apidest-bad-");
        var err = await Error(() => ev.CreateApiDestinationAsync(new CreateApiDestinationRequest
        {
            Name = badName,
            ConnectionArn = bogusConnArn,
            InvocationEndpoint = "https://example.com/webhook",
            HttpMethod = ApiDestinationHttpMethod.POST,
        }));
        (string?, string?) expected = (
            "ResourceNotFoundException",
            $"Failed to create the api-destination(s). Connection '{bogusConnArn}' does not exist.");
        Ensure(err == expected, $"expected {expected}, got {err}");
        var listResp = (await ev.ListApiDestinationsAsync(new ListApiDestinationsRequest { NamePrefix = badName })).ApiDestinations ?? [];
        Ensure(listResp.Count == 0, "rejected CreateApiDestination must not create a resource");
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
