// Translated from behavioral/events/check_create_api_destination_invalid_endpoint_pattern_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.create-api-destination-invalid-endpoint-pattern-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateApiDestinationInvalidEndpointPatternRejected
{
    // A field, not a literal: the SDK's analyzer refuses a literal that breaks the pattern.
    private static readonly string InvalidEndpoint = "https://sim2real.invalid/a b";

    private const string Pattern = @"^((%[0-9A-Fa-f]{2}|[-()_.!~*';/?:@\x26=+$,A-Za-z0-9])+)([).!';/?:,])?$";

    /// <summary>
    /// CreateApiDestination refuses an InvocationEndpoint carrying a character its documented
    /// pattern excludes, with ValidationException naming the value and the pattern, while a bare
    /// non-URL string that satisfies the pattern is still accepted and stored verbatim.
    ///
    /// Reference: https://docs.aws.amazon.com/eventbridge/latest/APIReference/API_CreateApiDestination.html
    /// </summary>
    [Test]
    [DisplayName("events.create-api-destination-invalid-endpoint-pattern-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var connName = FidelityContext.UniqueName("sim2real-behav-conn-");
        var connArn = (await events.CreateConnectionAsync(new CreateConnectionRequest
        {
            Name = connName,
            AuthorizationType = ConnectionAuthorizationType.API_KEY,
            AuthParameters = new CreateConnectionAuthRequestParameters
            {
                ApiKeyAuthParameters = new CreateConnectionApiKeyAuthRequestParameters { ApiKeyName = "x-api-key", ApiKeyValue = "sim2real-secret" },
            },
        })).ConnectionArn;
        ctx.OnCleanup(() => events.DeleteConnectionAsync(new DeleteConnectionRequest { Name = connName }));

        var badName = FidelityContext.UniqueName("sim2real-behav-dest-");
        var (code, message) = await Error(() => events.CreateApiDestinationAsync(new CreateApiDestinationRequest
        {
            Name = badName,
            ConnectionArn = connArn,
            InvocationEndpoint = InvalidEndpoint,
            HttpMethod = ApiDestinationHttpMethod.POST,
        }));
        (string?, string?) expected = (
            "ValidationException",
            $"1 validation error detected: Value '{InvalidEndpoint}' at 'invocationEndpoint' " +
            $"failed to satisfy constraint: Member must satisfy regular expression pattern: {Pattern}");
        Ensure(
            (code, message) == expected,
            $"expected the malformed endpoint to be refused with {expected}, got ({code}, {message})");
        var missing = await Error(() => events.DescribeApiDestinationAsync(new DescribeApiDestinationRequest { Name = badName }));
        Ensure(
            missing.Code == "ResourceNotFoundException",
            $"a rejected CreateApiDestination must not leave a destination behind, got {missing}");

        // Positive control: a non-URL string that still satisfies the pattern is accepted and stored
        // verbatim, so the rejection above is about the pattern, not about "not a URL".
        var goodName = FidelityContext.UniqueName("sim2real-behav-dest-");
        const string goodEndpoint = "sim2real-non-url-endpoint";
        await events.CreateApiDestinationAsync(new CreateApiDestinationRequest
        {
            Name = goodName,
            ConnectionArn = connArn,
            InvocationEndpoint = goodEndpoint,
            HttpMethod = ApiDestinationHttpMethod.POST,
        });
        ctx.OnCleanup(() => events.DeleteApiDestinationAsync(new DeleteApiDestinationRequest { Name = goodName }));
        var desc = await WaitActive(events, goodName, BudgetS(ctx, 60.0));
        Ensure(
            desc.ApiDestinationState?.Value == "ACTIVE",
            $"the api destination must settle ACTIVE, got '{desc.ApiDestinationState?.Value}'");
        Ensure(
            desc.InvocationEndpoint == goodEndpoint,
            $"expected the endpoint to be stored verbatim, got '{desc.InvocationEndpoint}'");
    }

    /// <summary>
    /// Call fn(); return the AWS error (Code, Message) if it raised a ClientError. An unexpected
    /// SUCCESS raises, because an emulator ACCEPTING input real AWS refuses is the strongest signal
    /// available.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected the call to be refused, but it succeeded");
        }

        return (error.Code ?? "", error.Message ?? "");
    }

    /// <summary>A wait's timeout, bounded by the runner's remaining time so the wait fails with its own message.</summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>Poll DescribeApiDestination until ApiDestinationState settles ACTIVE; raise on timeout.</summary>
    private static async Task<DescribeApiDestinationResponse> WaitActive(IAmazonEventBridge events, string name, double timeoutS)
    {
        var clock = FidelityTime.StartNew();
        var desc = await events.DescribeApiDestinationAsync(new DescribeApiDestinationRequest { Name = name });
        while (desc.ApiDestinationState?.Value != "ACTIVE")
        {
            if (clock.Elapsed.TotalSeconds >= timeoutS)
            {
                throw new TimeoutException(
                    $"api destination '{name}' did not settle ACTIVE within {timeoutS:0}s; " +
                    $"last state '{desc.ApiDestinationState?.Value}'");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
            desc = await events.DescribeApiDestinationAsync(new DescribeApiDestinationRequest { Name = name });
        }

        return desc;
    }
}
