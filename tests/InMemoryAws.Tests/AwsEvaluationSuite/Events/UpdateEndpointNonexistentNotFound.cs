// Translated from behavioral/events/check_update_endpoint_nonexistent_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.update-endpoint-nonexistent-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateEndpointNonexistentNotFound
{
    /// <summary>
    /// UpdateEndpoint targeting a name that was never created fails with
    /// ResourceNotFoundException (no phantom endpoint gets created), while updating a real
    /// endpoint's Description succeeds and persists.
    /// </summary>
    [Test]
    [DisplayName("events.update-endpoint-nonexistent-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var name = await CreateEndpoint(ctx);

        var bogus = FidelityContext.UniqueName("sim2real-behav-noendpoint-");
        var notFound = $"Endpoint {bogus} does not exist in region {ctx.Region}.";
        var (code, message) = await Error(() =>
            events.UpdateEndpointAsync(new UpdateEndpointRequest { Name = bogus, Description = "should-not-apply" }));
        Ensure(code == "ResourceNotFoundException", $"UpdateEndpoint on {bogus}: got {code}");
        Ensure(message == notFound, $"UpdateEndpoint message '{message}', expected '{notFound}'");

        // effect absent: the failed update did not create a resource under the bogus name
        var (absentCode, absentMessage) = await Error(() => events.DescribeEndpointAsync(new DescribeEndpointRequest { Name = bogus }));
        Ensure(
            absentCode == "ResourceNotFoundException",
            $"DescribeEndpoint on {bogus}: got {absentCode}");
        Ensure(
            absentMessage == notFound,
            $"DescribeEndpoint message '{absentMessage}', expected '{notFound}'");

        // positive control: updating the endpoint that actually exists succeeds and persists
        var newDescription = $"updated-{name}";
        await events.UpdateEndpointAsync(new UpdateEndpointRequest { Name = name, Description = newDescription });

        async Task<bool> Updated() => (await events.DescribeEndpointAsync(new DescribeEndpointRequest { Name = name })).Description == newDescription;
        Ensure(
            await PollUntil(Updated, timeoutS: 60.0, intervalS: 2.0),
            "endpoint description never reflected the successful UpdateEndpoint call");
    }

    /// <summary>
    /// Create a real global endpoint using only always-present default event buses and a
    /// real Route53 health check -- CreateEndpoint validates the health-check ARN against an
    /// actually-existing resource. Waits for the endpoint to leave CREATING (a mutating call
    /// against a still-provisioning endpoint fails with ConcurrentModificationException).
    /// </summary>
    /// <remarks>
    /// Route 53 is not emulated: the health check's ARN comes from
    /// <see cref="FidelityContext.HealthCheckArn"/>, and its creation, tags and cleanup are dropped.
    /// </remarks>
    private static async Task<string> CreateEndpoint(FidelityContext ctx)
    {
        var events = ctx.Events;
        var accountId = ctx.Account;
        // global endpoint failover requires a second, distinct region to route to -- not the region
        // under test, so this is a region-pinned resource requirement, not an assumption about ctx.region
        var secondaryRegion = ctx.Region != "us-west-2" ? "us-west-2" : "us-east-1";
        var name = FidelityContext.UniqueName("sim2real-behav-endpoint-");
        var healthCheckArn = FidelityContext.HealthCheckArn();
        await events.CreateEndpointAsync(new CreateEndpointRequest
        {
            Name = name,
            RoutingConfig = new RoutingConfig
            {
                FailoverConfig = new FailoverConfig
                {
                    Primary = new Primary { HealthCheck = healthCheckArn },
                    Secondary = new Secondary { Route = secondaryRegion },
                },
            },
            ReplicationConfig = new ReplicationConfig { State = ReplicationState.DISABLED },
            EventBuses =
            [
                new EndpointEventBus { EventBusArn = $"arn:aws:events:{ctx.Region}:{accountId}:event-bus/default" },
                new EndpointEventBus { EventBusArn = $"arn:aws:events:{secondaryRegion}:{accountId}:event-bus/default" },
            ],
        });

        // TagResource covers only rules and event buses (per the events service model's own docs);
        // EventBridge global endpoints have no tagging API, so this resource is untagged by necessity.
        ctx.OnCleanup(async () =>
        {
            var error = await FidelityContext.CaptureError(() => events.DeleteEndpointAsync(new DeleteEndpointRequest { Name = name }));
            if (error is not null && error.Code != "ResourceNotFoundException")
            {
                throw error.Exception;
            }
        });

        async Task<bool> Active() => (await events.DescribeEndpointAsync(new DescribeEndpointRequest { Name = name })).State == EndpointState.ACTIVE;
        Ensure(
            await PollUntil(Active, timeoutS: 90.0, intervalS: 3.0),
            $"endpoint {name} never reached ACTIVE within 90s");
        return name;
    }

    /// <summary>
    /// Call fn(); return its ClientError (code, message). An unexpected SUCCESS raises, because an
    /// over-permissive target accepting a call real AWS rejects is the strongest signal available.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected a ClientError, but the call SUCCEEDED");
        }

        return (error.Code, error.Message);
    }

    private static async Task<bool> PollUntil(Func<Task<bool>> predicate, double timeoutS, double intervalS)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            if (await predicate())
            {
                return true;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        return await predicate();
    }
}
