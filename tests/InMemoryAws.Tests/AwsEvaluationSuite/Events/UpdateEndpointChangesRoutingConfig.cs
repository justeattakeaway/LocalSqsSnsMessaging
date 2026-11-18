// Translated from behavioral/events/check_update_endpoint_changes_routing_config.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.update-endpoint-changes-routing-config</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateEndpointChangesRoutingConfig
{
    private const string Check = "update-endpoint-changes-routing-config";

    /// <summary>UpdateEndpoint mutates RoutingConfig; DescribeEndpoint reflects the new values, not the old ones.</summary>
    [Test]
    [DisplayName("events.update-endpoint-changes-routing-config")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        var busName = FidelityContext.UniqueName("sim2real-behav-bus-");
        List<Tag> BusTags() => [new Tag { Key = "sim2real-behav-check", Value = Check }];
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName, Tags = BusTags() })).EventBusArn;
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName })));

        // Empirically (verified against real AWS), CreateEndpoint validates at creation time that the
        // Route 53 health check ARN refers to a real health check, and the secondary-Region bus must
        // be real too -- provision both for real. ctx.client()'s `region` override keeps this on the
        // SAME target as the primary client.
        var secondaryRegion = PickSecondaryRegion(ctx.Region);
        var secondaryEvents = ctx.EventsClient(secondaryRegion);
        var secondaryBusArn = (await secondaryEvents.CreateEventBusAsync(new CreateEventBusRequest { Name = busName, Tags = BusTags() })).EventBusArn;
        ctx.OnCleanup(() => IgnoreNotFound(() => secondaryEvents.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName })));

        var initialHealthCheck = CreateHealthCheck();
        var updatedHealthCheck = CreateHealthCheck();

        var endpointName = FidelityContext.UniqueName("sim2real-behav-ep-");
        await events.CreateEndpointAsync(new CreateEndpointRequest
        {
            Name = endpointName,
            RoutingConfig = new RoutingConfig
            {
                FailoverConfig = new FailoverConfig
                {
                    Primary = new Primary { HealthCheck = initialHealthCheck },
                    Secondary = new Secondary { Route = secondaryRegion },
                },
            },
            EventBuses = [new EndpointEventBus { EventBusArn = busArn }, new EndpointEventBus { EventBusArn = secondaryBusArn }],
            ReplicationConfig = new ReplicationConfig { State = ReplicationState.DISABLED },
        });
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteEndpointAsync(new DeleteEndpointRequest { Name = endpointName })));

        var state = await PollEndpointState(events, endpointName, timeoutS: 60.0);
        Ensure(state == "ACTIVE", $"endpoint never reached ACTIVE, last observed state={state}");

        var created = await events.DescribeEndpointAsync(new DescribeEndpointRequest { Name = endpointName });
        Ensure(
            created.RoutingConfig.FailoverConfig.Secondary.Route == secondaryRegion,
            "assert created[\"RoutingConfig\"][\"FailoverConfig\"][\"Secondary\"][\"Route\"] == secondary_region");
        Ensure(
            created.RoutingConfig.FailoverConfig.Primary.HealthCheck == initialHealthCheck,
            "assert created[\"RoutingConfig\"][\"FailoverConfig\"][\"Primary\"][\"HealthCheck\"] == initial_health_check");

        await events.UpdateEndpointAsync(new UpdateEndpointRequest
        {
            Name = endpointName,
            RoutingConfig = new RoutingConfig
            {
                FailoverConfig = new FailoverConfig
                {
                    Primary = new Primary { HealthCheck = updatedHealthCheck },
                    Secondary = new Secondary { Route = secondaryRegion },
                },
            },
        });

        state = await PollEndpointState(events, endpointName, timeoutS: 60.0);
        Ensure(state == "ACTIVE", $"endpoint never returned to ACTIVE after update, last observed state={state}");

        var updated = await events.DescribeEndpointAsync(new DescribeEndpointRequest { Name = endpointName });
        var newHealthCheck = updated.RoutingConfig.FailoverConfig.Primary.HealthCheck;
        Ensure(newHealthCheck == updatedHealthCheck, "expected updated Primary.HealthCheck");
        Ensure(newHealthCheck != initialHealthCheck, "assert new_health_check != initial_health_check");
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
