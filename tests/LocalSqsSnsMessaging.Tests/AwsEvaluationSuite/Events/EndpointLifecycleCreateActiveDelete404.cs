// Translated from behavioral/events/check_endpoint_lifecycle_create_active_delete_404.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.endpoint-lifecycle-create-active-delete-404</summary>
[Category("AwsEvaluationSuite")]
public sealed class EndpointLifecycleCreateActiveDelete404
{
    private const string Check = "endpoint-lifecycle-create-active-delete-404";

    /// <summary>CreateEndpoint reaches ACTIVE (observed via DescribeEndpoint); DeleteEndpoint then 404s.</summary>
    [Test]
    [DisplayName("events.endpoint-lifecycle-create-active-delete-404")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        _ = ctx.Account;

        var busName = FidelityContext.UniqueName("sim2real-behav-bus-");
        List<Tag> BusTags() => [new Tag { Key = "sim2real-behav-check", Value = Check }];
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName, Tags = BusTags() })).EventBusArn;
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName })));

        // CreateEndpoint validates at creation time that
        // the Route 53 health check ARN refers to a real health check (ValidationException otherwise),
        // so provision one for real. The secondary-Region bus is created through a ctx client pinned
        // to that region.
        var secondaryRegion = PickSecondaryRegion(ctx.Region);
        var secondaryEvents = ctx.EventsClient(secondaryRegion);
        var secondaryBusArn = (await secondaryEvents.CreateEventBusAsync(new CreateEventBusRequest { Name = busName, Tags = BusTags() })).EventBusArn;
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

        await events.DeleteEndpointAsync(new DeleteEndpointRequest { Name = endpointName });

        (string?, string?) notFound = ("ResourceNotFoundException", $"Endpoint {endpointName} does not exist in region {ctx.Region}.");
        var clock = FidelityTime.StartNew();
        (string?, string?)? err = null;
        while (clock.Elapsed.TotalSeconds < 60.0)
        {
            var error = await FidelityContext.CaptureError(() => events.DescribeEndpointAsync(new DescribeEndpointRequest { Name = endpointName }));
            if (error is not null)
            {
                err = (error.Code, error.Message);
                if (error.Code == "ResourceNotFoundException")
                {
                    break;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        Ensure(err == notFound, $"expected {notFound} after delete, got {err?.ToString() ?? "None"}");

        // a deleted endpoint is not just unreadable -- it is also unmutable, never resurrected
        // by a write targeting its old name (UpdateEndpoint on a
        // never-existed name also raises ResourceNotFoundException; the deleted-name case must match).
        var mutateErr = await Error(() =>
            events.UpdateEndpointAsync(new UpdateEndpointRequest { Name = endpointName, Description = "post-delete-should-not-apply" }));
        Ensure(
            mutateErr == notFound,
            $"expected UpdateEndpoint on a deleted endpoint to raise {notFound}, got {mutateErr}");
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
