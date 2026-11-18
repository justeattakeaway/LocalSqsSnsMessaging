// Translated from behavioral/events/check_delete_endpoint_nonexistent_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.delete-endpoint-nonexistent-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteEndpointNonexistentNotFound
{
    /// <summary>
    /// DeleteEndpoint on a never-created name succeeds (despite the documented ResourceNotFound),
    /// stably across two names, without disturbing a real endpoint, which then deletes normally.
    /// </summary>
    [Test]
    [DisplayName("events.delete-endpoint-nonexistent-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var name = await CreateEndpoint(ctx);

        var bogus = FidelityContext.UniqueName("sim2real-behav-noendpoint-");
        // A ClientError here fails the check: real AWS answers success.
        await events.DeleteEndpointAsync(new DeleteEndpointRequest { Name = bogus });

        // repeat with a distinct nonexistent name: the idempotent-success result is stable
        var otherBogus = FidelityContext.UniqueName("sim2real-behav-noendpoint-");
        await events.DeleteEndpointAsync(new DeleteEndpointRequest { Name = otherBogus });

        // effect absent: the silently-succeeded delete on bogus names did not touch our real endpoint
        var described = await events.DescribeEndpointAsync(new DescribeEndpointRequest { Name = name });
        Ensure(described.Name == name, $"expected real endpoint '{name}' unaffected, got '{described.Name}'");

        // positive control: deleting the endpoint that actually exists succeeds
        await events.DeleteEndpointAsync(new DeleteEndpointRequest { Name = name });

        async Task<bool> Gone()
        {
            var error = await FidelityContext.CaptureError(() => events.DescribeEndpointAsync(new DescribeEndpointRequest { Name = name }));
            if (error is null)
            {
                return false;
            }

            return error.Code == "ResourceNotFoundException" ? true : throw error.Exception;
        }

        Ensure(
            await PollUntil(Gone, timeoutS: 60.0, intervalS: 2.0),
            "endpoint still describable after a successful DeleteEndpoint call");
    }

    /// <summary>
    /// Create a global endpoint over the default buses and wait for ACTIVE; mutating a
    /// still-provisioning endpoint fails with ConcurrentModificationException.
    /// </summary>
    /// <remarks>
    /// Route 53 is not emulated: the health check's ARN comes from
    /// <see cref="FidelityContext.HealthCheckArn"/>, and its creation and cleanup are dropped.
    /// </remarks>
    private static async Task<string> CreateEndpoint(FidelityContext ctx)
    {
        var events = ctx.Events;
        var accountId = ctx.Account;
        // region literals: a global endpoint's RoutingConfig requires a real, distinct secondary
        // Region to fail over to, not the check's own region pin.
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

        ctx.OnCleanup(async () =>
        {
            var error = await FidelityContext.CaptureError(() => events.DeleteEndpointAsync(new DeleteEndpointRequest { Name = name }));
            if (error is not null && error.Code != "ResourceNotFoundException")
            {
                throw error.Exception;
            }
        });

        async Task<bool> Active() => (await events.DescribeEndpointAsync(new DescribeEndpointRequest { Name = name })).State == EndpointState.ACTIVE;
        await PollUntil(Active, timeoutS: 90.0, intervalS: 3.0);
        return name;
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
