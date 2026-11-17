// Translated from behavioral/events/check_create_endpoint_malformed_routing_config_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.create-endpoint-malformed-routing-config-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateEndpointMalformedRoutingConfigRejected
{
    private const string Check = "create-endpoint-malformed-routing-config-rejected";

    /// <summary>
    /// CreateEndpoint whose FailoverConfig omits the required Secondary is rejected with a
    /// ValidationException and creates no describable or listable endpoint.
    /// </summary>
    [Test]
    [DisplayName("events.create-endpoint-malformed-routing-config-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        // Client-side validation would catch the missing member; the .NET SDK never checks it, so
        // the ordinary client sends it and the server validates.
        var rawEvents = ctx.Events;

        var endpointName = FidelityContext.UniqueName("sim2real-behav-ep-");

        var err = await Error(() => rawEvents.CreateEndpointAsync(new CreateEndpointRequest
        {
            Name = endpointName,
            RoutingConfig = new RoutingConfig
            {
                FailoverConfig = new FailoverConfig
                {
                    // Secondary is a required member of FailoverConfig -- omitted here.
                    Primary = new Primary
                    {
                        HealthCheck = "arn:aws:route53:::healthcheck/00000000-0000-0000-0000-000000000000",
                    },
                },
            },
            EventBuses =
            [
                new EndpointEventBus { EventBusArn = $"arn:aws:events:{ctx.Region}:111122223333:event-bus/sim2real-fake-a" },
                new EndpointEventBus { EventBusArn = $"arn:aws:events:{ctx.Region}:111122223333:event-bus/sim2real-fake-b" },
            ],
        }));
        (string?, string?) expected = (
            "ValidationException",
            "1 validation error detected: Value null at 'routingConfig.failoverConfig.secondary' " +
            "failed to satisfy constraint: Member must not be null");
        Ensure(err == expected, $"expected {expected}, got {err}");

        // Effect absent: the rejected create must not have produced a describable endpoint.
        var describeErr = await Error(() => events.DescribeEndpointAsync(new DescribeEndpointRequest { Name = endpointName }));
        (string?, string?) expectedAbsent = (
            "ResourceNotFoundException",
            $"Endpoint {endpointName} does not exist in region {ctx.Region}.");
        Ensure(
            describeErr == expectedAbsent,
            $"malformed CreateEndpoint must not create an endpoint, describe returned {describeErr}");
        var names = ((await events.ListEndpointsAsync(new ListEndpointsRequest())).Endpoints ?? []).Select(ep => ep.Name).ToHashSet();
        Ensure(
            !names.Contains(endpointName),
            $"malformed CreateEndpoint must not create a listable endpoint, found '{endpointName}' " +
            "in ListEndpoints");

        // Positive control: the same credentials can still perform an ordinary, well-formed control-
        // plane operation -- this is a targeted input-validation rejection, not a broken client.
        var busName = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName, Tags = [new Tag { Key = "sim2real", Value = Check }] });
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName })));
        var described = await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = busName });
        Ensure(
            described.Name == busName,
            $"expected positive-control event bus Name='{busName}', got '{described.Name}'");
    }

    /// <summary>Call fn() and return its (code, message); an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected a ClientError, but the call SUCCEEDED");
        }

        return (error.Code, error.Message);
    }

    private static async Task IgnoreNotFound(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }
}
