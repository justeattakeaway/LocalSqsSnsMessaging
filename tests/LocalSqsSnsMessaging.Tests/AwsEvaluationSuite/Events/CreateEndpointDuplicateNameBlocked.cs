// Translated from behavioral/events/check_create_endpoint_duplicate_name_blocked.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.create-endpoint-duplicate-name-blocked</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateEndpointDuplicateNameBlocked
{
    /// <summary>
    /// CreateEndpoint with a Name that already belongs to an existing global endpoint is
    /// rejected with ResourceAlreadyExistsException instead of silently succeeding or overwriting
    /// the original endpoint's configuration.
    /// </summary>
    [Test]
    [DisplayName("events.create-endpoint-duplicate-name-blocked")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        var busName = FidelityContext.UniqueName("sim2real-behav-bus-");
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName, Tags = [new Tag { Key = "sim2real", Value = "1" }] })).EventBusArn;
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName })));

        // Empirically, CreateEndpoint validates at creation time that the Route 53 health check ARN
        // refers to a real health check, and the secondary-Region bus must be real too -- provision both
        // for real.
        var secondaryRegion = PickSecondaryRegion(ctx.Region);
        var secondaryEvents = ctx.EventsClient(secondaryRegion);
        var secondaryBusArn = (await secondaryEvents.CreateEventBusAsync(new CreateEventBusRequest { Name = busName, Tags = [new Tag { Key = "sim2real", Value = "1" }] })).EventBusArn;
        ctx.OnCleanup(() => IgnoreNotFound(() => secondaryEvents.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName })));

        var healthCheckArn = CreateHealthCheck();

        RoutingConfig RoutingConfig() => new()
        {
            FailoverConfig = new FailoverConfig
            {
                Primary = new Primary { HealthCheck = healthCheckArn },
                Secondary = new Secondary { Route = secondaryRegion },
            },
        };
        List<EndpointEventBus> EventBuses() => [new EndpointEventBus { EventBusArn = busArn }, new EndpointEventBus { EventBusArn = secondaryBusArn }];

        var endpointName = FidelityContext.UniqueName("sim2real-behav-ep-");
        await events.CreateEndpointAsync(new CreateEndpointRequest
        {
            Name = endpointName,
            RoutingConfig = RoutingConfig(),
            EventBuses = EventBuses(),
            ReplicationConfig = new ReplicationConfig { State = ReplicationState.DISABLED },
            Description = "original",
        });
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteEndpointAsync(new DeleteEndpointRequest { Name = endpointName })));
        var state = await PollEndpointState(events, endpointName, timeoutS: 60.0);
        Ensure(state == "ACTIVE", $"endpoint never reached ACTIVE, last observed state={state}");

        var err = await Error(() => events.CreateEndpointAsync(new CreateEndpointRequest
        {
            Name = endpointName,
            RoutingConfig = RoutingConfig(),
            EventBuses = EventBuses(),
            ReplicationConfig = new ReplicationConfig { State = ReplicationState.DISABLED },
            Description = "duplicate-attempt",
        }));
        (string?, string?) expected = ("ResourceAlreadyExistsException", $"Endpoint {endpointName} already exists.");
        Ensure(err == expected, $"expected {expected}, got {err}");

        // Effect absent: the original endpoint's Description was NOT overwritten by the rejected
        // duplicate-name create call.
        var described = await events.DescribeEndpointAsync(new DescribeEndpointRequest { Name = endpointName });
        Ensure(
            described.Description == "original",
            "duplicate CreateEndpoint must not mutate the existing endpoint, got Description=" +
            $"'{described.Description}'");

        // Positive control: CreateEndpoint with a genuinely different Name (same underlying buses
        // and health check) still succeeds -- proving CreateEndpoint works in general, and the
        // rejection above is specifically about the name collision.
        var otherName = FidelityContext.UniqueName("sim2real-behav-ep2-");
        await events.CreateEndpointAsync(new CreateEndpointRequest
        {
            Name = otherName,
            RoutingConfig = RoutingConfig(),
            EventBuses = EventBuses(),
            ReplicationConfig = new ReplicationConfig { State = ReplicationState.DISABLED },
        });
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteEndpointAsync(new DeleteEndpointRequest { Name = otherName })));
        var otherState = await PollEndpointState(events, otherName, timeoutS: 60.0);
        Ensure(otherState == "ACTIVE", $"control endpoint never reached ACTIVE, got {otherState}");
    }

    /// <summary>
    /// A pool of real, distinct AWS regions to pick a SECONDARY region from -- EventBridge global
    /// endpoints require the failover Secondary.Route to be a genuine other region.
    /// </summary>
    private static readonly string[] RegionPool = ["us-east-1", "us-west-2", "eu-west-1", "ap-southeast-2"];

    private static string PickSecondaryRegion(string primaryRegion) =>
        RegionPool.FirstOrDefault(region => region != primaryRegion) ?? "us-east-1";

    /// <summary>
    /// Route 53 is not emulated: the health check's ARN comes from
    /// <see cref="FidelityContext.HealthCheckArn"/>, and its creation, tags and cleanup are dropped.
    /// </summary>
    private static string CreateHealthCheck() => FidelityContext.HealthCheckArn();

    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected rejection, but the call succeeded");
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

    private static async Task<string?> PollEndpointState(Amazon.EventBridge.IAmazonEventBridge events, string name, double timeoutS = 60.0, double intervalS = 2.0)
    {
        var clock = FidelityTime.StartNew();
        string? state = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            state = (await events.DescribeEndpointAsync(new DescribeEndpointRequest { Name = name })).State?.Value;
            if (state is "ACTIVE" or "CREATE_FAILED" or "DELETE_FAILED" or "UPDATE_FAILED")
            {
                return state;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        return state;
    }
}
