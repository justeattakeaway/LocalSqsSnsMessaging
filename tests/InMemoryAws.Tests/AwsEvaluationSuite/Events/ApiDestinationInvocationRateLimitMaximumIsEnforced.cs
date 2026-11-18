// Translated from behavioral/events/check_api_destination_invocation_rate_limit_maximum_is_enforced.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.api-destination-invocation-rate-limit-maximum-is-enforced</summary>
[Category("AwsEvaluationSuite")]
public sealed class ApiDestinationInvocationRateLimitMaximumIsEnforced
{
    private const int Ceiling = 1024; // bisection ceiling: comfortably above the documented 300
    private const int Floor = 10; // sanity floor: the documented range is 1..300, so a maximum below 10 is a target bug
    private const double AuthorizedTimeoutS = 120.0;
    private const double ActiveTimeoutS = 90.0;
    private const double ConflictRetryS = 20.0;
    private const string Endpoint = "https://192.0.2.1/sim2real-hook";

    /// <summary>
    /// InvocationRateLimitPerSecond has a finite maximum, enforced identically by Create and Update
    /// with LimitExceededException; the maximum is stored verbatim, a refused write changes nothing,
    /// and an invalid HttpMethod is refused without disturbing the destination.
    /// </summary>
    [Test]
    [DisplayName("events.api-destination-invocation-rate-limit-maximum-is-enforced")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        var connName = FidelityContext.UniqueName("sim2real-behav-conn-");
        var conn = await events.CreateConnectionAsync(new CreateConnectionRequest
        {
            Name = connName,
            AuthorizationType = ConnectionAuthorizationType.API_KEY,
            AuthParameters = new CreateConnectionAuthRequestParameters
            {
                ApiKeyAuthParameters = new CreateConnectionApiKeyAuthRequestParameters { ApiKeyName = "x-api-key", ApiKeyValue = "sim2real-not-a-secret" },
            },
        });
        ctx.OnCleanup(() => DeleteQuietly(ctx, "connection", connName));
        var authorized = await PollUntil(
            () => events.DescribeConnectionAsync(new DescribeConnectionRequest { Name = connName }),
            d => d.ConnectionState?.Value is "AUTHORIZED" or "CREATE_FAILED",
            timeoutS: AuthorizedTimeoutS,
            intervalS: 2.0,
            description: $"connection {connName} leaves CREATING");
        Ensure(
            authorized.ConnectionState?.Value == "AUTHORIZED",
            "an API_KEY connection must reach AUTHORIZED before a destination can use it, got " +
            $"'{authorized.ConnectionState?.Value}' StateReason='{authorized.StateReason}'");

        // Positive control: the smallest legal value is accepted and echoed exactly.
        var dest = FidelityContext.UniqueName("sim2real-behav-apidest-");
        await events.CreateApiDestinationAsync(new CreateApiDestinationRequest
        {
            Name = dest,
            ConnectionArn = conn.ConnectionArn,
            InvocationEndpoint = Endpoint,
            HttpMethod = ApiDestinationHttpMethod.POST,
            InvocationRateLimitPerSecond = 1,
        });
        ctx.OnCleanup(() => DeleteQuietly(ctx, "api_destination", dest));
        await WaitActive(events, dest);
        Ensure(
            await StoredRate(events, dest) == 1,
            "positive control failed: InvocationRateLimitPerSecond=1 must be stored verbatim, " +
            $"DescribeApiDestination reports {await StoredRate(events, dest)}");

        // The ceiling must be refused first so the bisection interval is bracketed on both sides.
        var overCeiling = await UpdateRateLimit(events, dest, Ceiling);
        Ensure(
            overCeiling is not null,
            $"InvocationRateLimitPerSecond={Ceiling} was ACCEPTED — real EventBridge documents a " +
            "maximum of 300 events per second, so a target with no maximum at all is what this " +
            "measures");
        var updateMax = await MaxAcceptedRate(events, dest);
        Ensure(
            updateMax is >= Floor and < Ceiling,
            "the measured maximum InvocationRateLimitPerSecond must be finite and no smaller than " +
            $"{Floor} (the documented range is 1..300); measured {updateMax}");
        var expectedCeiling = RateRefusal("update", Ceiling, dest, updateMax);
        Ensure(
            overCeiling == expectedCeiling,
            $"UpdateApiDestination rate={Ceiling} must be refused with {expectedCeiling}, " +
            $"got {overCeiling}");

        // The boundary value is stored verbatim, not clamped.
        var conflict = await UpdateRateLimit(events, dest, updateMax);
        Ensure(
            conflict is null,
            $"re-applying the measured maximum {updateMax} must succeed, got {conflict}");
        await WaitActive(events, dest);
        Ensure(
            await StoredRate(events, dest) == updateMax,
            $"the maximum accepted rate limit must round-trip verbatim: set {updateMax}, " +
            $"DescribeApiDestination reports {await StoredRate(events, dest)} — a silent clamp changes " +
            "delivery behaviour, which no other read observes");

        // One over the maximum is refused and leaves the stored value untouched.
        var refusedUpdate = await UpdateRateLimit(events, dest, updateMax + 1);
        Ensure(
            refusedUpdate is not null,
            $"UpdateApiDestination accepted {updateMax + 1} immediately after refusing it during the " +
            "bisection — the maximum is not applied consistently");
        var expectedUpdateRefusal = RateRefusal("update", updateMax + 1, dest, updateMax);
        Ensure(
            refusedUpdate == expectedUpdateRefusal,
            $"UpdateApiDestination rate={updateMax + 1} must be refused with " +
            $"{expectedUpdateRefusal}, got {refusedUpdate}");
        Ensure(
            await StoredRate(events, dest) == updateMax,
            "the refused update must not have written: expected the stored rate limit to still be " +
            $"{updateMax}, DescribeApiDestination reports {await StoredRate(events, dest)}");

        // The create path must enforce the same maximum, in both directions.
        var atMaxName = FidelityContext.UniqueName("sim2real-behav-apidest-");
        await events.CreateApiDestinationAsync(new CreateApiDestinationRequest
        {
            Name = atMaxName,
            ConnectionArn = conn.ConnectionArn,
            InvocationEndpoint = Endpoint,
            HttpMethod = ApiDestinationHttpMethod.POST,
            InvocationRateLimitPerSecond = updateMax,
        });
        ctx.OnCleanup(() => DeleteQuietly(ctx, "api_destination", atMaxName));
        await WaitActive(events, atMaxName);
        Ensure(
            await StoredRate(events, atMaxName) == updateMax,
            "CreateApiDestination must accept and store the same maximum the update path accepts: set " +
            $"{updateMax}, got {await StoredRate(events, atMaxName)}");

        var overMaxName = FidelityContext.UniqueName("sim2real-behav-apidest-");
        var createRefusal = await Error(() => events.CreateApiDestinationAsync(new CreateApiDestinationRequest
        {
            Name = overMaxName,
            ConnectionArn = conn.ConnectionArn,
            InvocationEndpoint = Endpoint,
            HttpMethod = ApiDestinationHttpMethod.POST,
            InvocationRateLimitPerSecond = updateMax + 1,
        }));
        var expectedCreateRefusal = RateRefusal("create", updateMax + 1, overMaxName, updateMax);
        Ensure(
            createRefusal == expectedCreateRefusal,
            $"CreateApiDestination must refuse InvocationRateLimitPerSecond={updateMax + 1} exactly as " +
            $"UpdateApiDestination does: expected {expectedCreateRefusal}, got {createRefusal}");
        var absent = await Error(() => events.DescribeApiDestinationAsync(new DescribeApiDestinationRequest { Name = overMaxName }));
        (string?, string?) expectedAbsent = (
            "ResourceNotFoundException",
            $"Failed to describe the api-destination(s). An api-destination '{overMaxName}' " +
            "does not exist.");
        Ensure(
            absent == expectedAbsent,
            "the refused CreateApiDestination must not have created anything: " +
            $"DescribeApiDestination({overMaxName}) returned {absent}, expected {expectedAbsent}");
        var listed = (await events.ListApiDestinationsAsync(new ListApiDestinationsRequest { NamePrefix = overMaxName })).ApiDestinations ?? [];
        Ensure(
            listed.Count == 0,
            "the refused CreateApiDestination must leave nothing in " +
            $"ListApiDestinations(NamePrefix='{overMaxName}'), got [{string.Join(", ", listed.Select(d => d.Name))}]");

        // botocore does not validate Smithy enums, so HttpMethod="BOGUS" reaches the service.
        var badMethod = await Error(() => events.UpdateApiDestinationAsync(new UpdateApiDestinationRequest
        {
            Name = dest,
            HttpMethod = ApiDestinationHttpMethod.FindValue("BOGUS"),
        }));
        Ensure(
            badMethod.Code is not null,
            "UpdateApiDestination ACCEPTED HttpMethod='BOGUS' — botocore does not validate Smithy enums, " +
            "so this request really did reach the service and the service must refuse it");
        var described = await events.DescribeApiDestinationAsync(new DescribeApiDestinationRequest { Name = dest });
        Ensure(
            described.HttpMethod?.Value == "POST",
            "the refused HttpMethod update must not have written: expected POST, got " +
            $"'{described.HttpMethod?.Value}'");
        Ensure(
            described.InvocationRateLimitPerSecond == updateMax,
            "the refused HttpMethod update must not have disturbed the rate limit either: expected " +
            $"{updateMax}, got {described.InvocationRateLimitPerSecond}");
    }

    /// <summary>(code, message) for a call expected to fail; an unexpected SUCCESS raises.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected a ClientError, but the call SUCCEEDED");
        }

        return (error.Code, error.Message);
    }

    /// <summary>The refusal EventBridge returns for a rate limit above the maximum on create or update.</summary>
    private static (string?, string?) RateRefusal(string verb, int rate, string name, int maximum) => (
        "LimitExceededException",
        $"Failed to {verb} the api-destination(s). Invalid Invocation Rate Limit: Rate specified " +
        $"'{rate}' for '{name}' exceeds maximum allowed of '{maximum}'.");

    /// <summary>Poll until predicate holds; RAISE on timeout (never return the last value).</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, double intervalS, string description)
    {
        var clock = FidelityTime.StartNew();
        T? last = default;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not met within {timeoutS}s. Last result: {last}");
    }

    private static async Task WaitActive(IAmazonEventBridge events, string name)
    {
        var state = await PollUntil(
            () => events.DescribeApiDestinationAsync(new DescribeApiDestinationRequest { Name = name }),
            d => d.ApiDestinationState?.Value is "ACTIVE" or "INACTIVE",
            timeoutS: ActiveTimeoutS,
            intervalS: 2.0,
            description: $"api destination {name} settles");
        Ensure(
            state.ApiDestinationState?.Value == "ACTIVE",
            $"api destination {name} must reach ACTIVE, got '{state.ApiDestinationState?.Value}'");
    }

    /// <summary>The rate limit as read back by DescribeApiDestination; an absent field fails loudly.</summary>
    private static async Task<int> StoredRate(IAmazonEventBridge events, string name) =>
        (await events.DescribeApiDestinationAsync(new DescribeApiDestinationRequest { Name = name })).InvocationRateLimitPerSecond
        ?? throw Fail("DescribeApiDestination has no InvocationRateLimitPerSecond");

    /// <summary>
    /// None if the update was accepted, else (code, message). ConcurrentModificationException from
    /// back-to-back bisection updates is retried under a deadline, not counted as a rejection.
    /// </summary>
    private static async Task<(string? Code, string? Message)?> UpdateRateLimit(IAmazonEventBridge events, string name, int value)
    {
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() =>
                events.UpdateApiDestinationAsync(new UpdateApiDestinationRequest { Name = name, InvocationRateLimitPerSecond = value }));
            if (error is null)
            {
                return null;
            }

            if (error.Code != "ConcurrentModificationException")
            {
                return (error.Code, error.Message);
            }

            if (clock.Elapsed.TotalSeconds >= ConflictRetryS)
            {
                throw Fail(
                    $"UpdateApiDestination({name}, rate={value}) kept returning " +
                    $"ConcurrentModificationException for {ConflictRetryS}s, so the probe could not " +
                    "be classified as accepted or refused");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }

    /// <summary>
    /// Bisect [1, _CEILING] for the greatest rate limit UpdateApiDestination accepts;
    /// the caller has already seen 1 accepted and _CEILING refused.
    /// </summary>
    private static async Task<int> MaxAcceptedRate(IAmazonEventBridge events, string name)
    {
        var (low, high) = (1, Ceiling); // accepted, refused
        while (high - low > 1)
        {
            var mid = (low + high) / 2;
            if (await UpdateRateLimit(events, name, mid) is null)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    /// <summary>Delete a connection or API destination; any error other than not-found raises.</summary>
    private static async Task DeleteQuietly(FidelityContext ctx, string kind, string name)
    {
        var events = ctx.Events;
        Func<Task> call = kind == "connection"
            ? () => events.DeleteConnectionAsync(new DeleteConnectionRequest { Name = name })
            : () => events.DeleteApiDestinationAsync(new DeleteApiDestinationRequest { Name = name });
        var error = await FidelityContext.CaptureError(call);
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw new InvalidOperationException($"could not delete {kind} {name}: {error.Code} {error.Message}", error.Exception);
        }
    }
}
