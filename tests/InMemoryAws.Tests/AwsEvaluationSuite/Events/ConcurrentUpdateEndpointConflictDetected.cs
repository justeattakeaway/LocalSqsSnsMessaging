// Translated from behavioral/events/check_concurrent_update_endpoint_conflict_detected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.concurrent-update-endpoint-conflict-detected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentUpdateEndpointConflictDetected
{
    private const string Check = "concurrent-update-endpoint-conflict-detected";

    /// <summary>
    /// Many overlapping UpdateEndpoint calls at one global endpoint leave at least one call
    /// succeeding (ConcurrentModificationException is a possible, not a guaranteed, outcome for the
    /// rest), and the endpoint settles back to ACTIVE with the winner's value durably persisted.
    /// </summary>
    [Test]
    [DisplayName("events.concurrent-update-endpoint-conflict-detected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        var busName = FidelityContext.UniqueName("sim2real-behav-bus-");
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName, Tags = [new Tag { Key = "sim2real", Value = Check }] })).EventBusArn;
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName })));

        // Empirically, CreateEndpoint validates at creation time that the Route 53 health check ARN
        // refers to a real health check, and the secondary-Region bus must be real too -- provision both
        // for real. ctx.client(region=...) pins a different region on the SAME target (real AWS or
        // emulator), unlike a hand-rolled boto3.client() which would silently drop emulator targeting.
        var secondaryRegion = PickSecondaryRegion(ctx.Region);
        var secondaryEvents = ctx.EventsClient(secondaryRegion);
        var secondaryBusArn = (await secondaryEvents.CreateEventBusAsync(new CreateEventBusRequest { Name = busName, Tags = [new Tag { Key = "sim2real", Value = Check }] })).EventBusArn;
        ctx.OnCleanup(() => IgnoreNotFound(() => secondaryEvents.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName })));

        var healthCheckArn = CreateHealthCheck();

        var endpointName = FidelityContext.UniqueName("sim2real-behav-ep-");
        await events.CreateEndpointAsync(new CreateEndpointRequest
        {
            Name = endpointName,
            RoutingConfig = new RoutingConfig
            {
                FailoverConfig = new FailoverConfig
                {
                    Primary = new Primary { HealthCheck = healthCheckArn },
                    Secondary = new Secondary { Route = secondaryRegion },
                },
            },
            EventBuses = [new EndpointEventBus { EventBusArn = busArn }, new EndpointEventBus { EventBusArn = secondaryBusArn }],
            ReplicationConfig = new ReplicationConfig { State = ReplicationState.DISABLED },
        });
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteEndpointAsync(new DeleteEndpointRequest { Name = endpointName })));

        var state = await PollEndpointState(events, endpointName, timeoutS: 60.0);
        Ensure(state == "ACTIVE", $"endpoint never reached ACTIVE, last observed state={state}");

        const int racerCount = 10;
        const int maxBursts = 6;
        var successSeen = false;
        for (var burst = 0; burst < maxBursts; burst++)
        {
            var b = burst;
            var calls = Enumerable.Range(0, racerCount)
                .Select(i => (Func<Task>)(() => events.UpdateEndpointAsync(new UpdateEndpointRequest { Name = endpointName, Description = $"race-{b}-{i}" })))
                .ToList();
            var codes = await FireConcurrent(ctx, calls);
            if (codes.Any(code => code is null))
            {
                successSeen = true;
            }

            if (codes.Any(code => code == "ConcurrentModificationException"))
            {
                break;
            }

            // let the endpoint settle back to ACTIVE before the next burst
            await PollEndpointState(events, endpointName, timeoutS: 30.0);
        }

        // ConcurrentModificationException is a possible UpdateEndpoint error, not a guaranteed one, so
        // a backend that serializes the burst passes on the success it must still deliver.
        Ensure(successSeen, "expected at least one UpdateEndpoint call in the race to succeed");

        var finalState = await PollEndpointState(events, endpointName, timeoutS: 60.0);
        Ensure(finalState == "ACTIVE", $"endpoint did not settle back to ACTIVE, got {finalState}");

        // Positive control: a solo UpdateEndpoint (no contention) still succeeds and persists.
        await events.UpdateEndpointAsync(new UpdateEndpointRequest { Name = endpointName, Description = "solo-update-control" });
        var controlState = await PollEndpointState(events, endpointName, timeoutS: 60.0);
        Ensure(controlState == "ACTIVE", $"solo control update left endpoint in {controlState}");
        var described = await events.DescribeEndpointAsync(new DescribeEndpointRequest { Name = endpointName });
        Ensure(
            described.Description == "solo-update-control",
            $"expected solo-update-control, got '{described.Description}'");
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

    /// <summary>
    /// Submit every callable before blocking on any result, then collect the error code (or
    /// None on success) for each -- maximizes true overlap on the wire.
    /// </summary>
    private static async Task<List<string?>> FireConcurrent(FidelityContext ctx, List<Func<Task>> fns)
    {
        using var _ = ctx.Concurrently();
        var futures = fns.Select(fn => Task.Run(async () => (await FidelityContext.CaptureError(fn))?.Code)).ToList();
        return [.. await Task.WhenAll(futures)];
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
