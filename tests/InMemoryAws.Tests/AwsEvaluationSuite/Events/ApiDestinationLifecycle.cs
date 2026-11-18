// Translated from behavioral/events/check_api_destination_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.api-destination-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class ApiDestinationLifecycle
{
    /// <summary>
    /// An API destination on a real connection settles ACTIVE and is listable and describable;
    /// UpdateApiDestination mutates it in place and DeleteApiDestination makes it disappear.
    /// The connection AUTHORIZED and destination ACTIVE settles are asynchronous, so both are polled.
    /// </summary>
    [Test]
    [DisplayName("events.api-destination-lifecycle")]
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
        var authorized = await Wait(() => ev.DescribeConnectionAsync(new DescribeConnectionRequest { Name = connName }),
            d => d.ConnectionState?.Value is "AUTHORIZED" or "CREATE_FAILED", 60);
        Ensure(
            authorized.ConnectionState?.Value == "AUTHORIZED",
            $"the API_KEY connection must settle AUTHORIZED, got '{authorized.ConnectionState?.Value}' " +
            $"StateReason='{authorized.StateReason}'");

        var destName = FidelityContext.UniqueName("sim2real-behav-apidest-");
        var created = await ev.CreateApiDestinationAsync(new CreateApiDestinationRequest
        {
            Name = destName,
            ConnectionArn = conn.ConnectionArn,
            InvocationEndpoint = "https://example.com/webhook",
            HttpMethod = ApiDestinationHttpMethod.POST,
            InvocationRateLimitPerSecond = 10,
        });
        ctx.OnCleanup(() => CleanupApiDestination(ev, destName));
        Ensure(created.ApiDestinationState?.Value is "ACTIVE" or "INACTIVE", $"unexpected state: {created.ApiDestinationState?.Value}");
        await Wait(() => ev.DescribeApiDestinationAsync(new DescribeApiDestinationRequest { Name = destName }),
            d => d.ApiDestinationState?.Value == "ACTIVE", 60);

        var desc = await ev.DescribeApiDestinationAsync(new DescribeApiDestinationRequest { Name = destName });
        Ensure(
            desc.ApiDestinationState?.Value == "ACTIVE",
            $"the api destination must settle ACTIVE, got '{desc.ApiDestinationState?.Value}'");
        Ensure(desc.HttpMethod?.Value == "POST", $"expected HttpMethod POST, got {desc.HttpMethod?.Value}");
        Ensure(
            desc.InvocationRateLimitPerSecond == 10,
            $"expected InvocationRateLimitPerSecond 10, got {desc.InvocationRateLimitPerSecond}");
        Ensure(
            desc.ConnectionArn == conn.ConnectionArn,
            $"expected ConnectionArn {conn.ConnectionArn}, got {desc.ConnectionArn}");

        var listed = ((await ev.ListApiDestinationsAsync(new ListApiDestinationsRequest { NamePrefix = destName })).ApiDestinations ?? [])
            .Select(d => d.Name).ToHashSet();
        Ensure(listed.SetEquals([destName]), $"NamePrefix filter broken: [{string.Join(", ", listed)}]");

        await ev.UpdateApiDestinationAsync(new UpdateApiDestinationRequest { Name = destName, HttpMethod = ApiDestinationHttpMethod.PUT, InvocationRateLimitPerSecond = 5 });
        await Wait(() => ev.DescribeApiDestinationAsync(new DescribeApiDestinationRequest { Name = destName }),
            d => d.HttpMethod?.Value == "PUT", 60);
        var updated = await ev.DescribeApiDestinationAsync(new DescribeApiDestinationRequest { Name = destName });
        Ensure(updated.HttpMethod?.Value == "PUT", $"http method not updated: {updated.HttpMethod?.Value}");
        Ensure(updated.InvocationRateLimitPerSecond == 5, $"rate limit not updated: {updated.InvocationRateLimitPerSecond}");
        Ensure(updated.ApiDestinationArn == created.ApiDestinationArn, "ARN must be stable");

        await ev.DeleteApiDestinationAsync(new DeleteApiDestinationRequest { Name = destName });
        var err = await Error(() => ev.DescribeApiDestinationAsync(new DescribeApiDestinationRequest { Name = destName }));
        (string?, string?) expected = (
            "ResourceNotFoundException",
            $"Failed to describe the api-destination(s). An api-destination '{destName}' does not exist.");
        Ensure(err == expected, $"expected {expected}, got {err}");

        err = await Error(() => ev.UpdateApiDestinationAsync(new UpdateApiDestinationRequest { Name = destName, HttpMethod = ApiDestinationHttpMethod.GET }));
        expected = (
            "ResourceNotFoundException",
            $"Failed to update the api-destination(s). An api-destination '{destName}' does not exist.");
        Ensure(
            err == expected,
            $"a mutation on the deleted destination must also fail not-found with {expected}, got {err}");
    }

    /// <summary>Call fn(); return its (code, message). An unexpected SUCCESS raises.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected a ClientError, but the call SUCCEEDED");
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

    /// <summary>Poll describe() until predicate holds; raise on timeout, never return the stale value.</summary>
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
