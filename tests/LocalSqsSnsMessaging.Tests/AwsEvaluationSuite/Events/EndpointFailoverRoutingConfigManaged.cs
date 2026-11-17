// Translated from behavioral/events/check_endpoint_failover_routing_config_managed.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.endpoint-failover-routing-config-managed</summary>
[Category("AwsEvaluationSuite")]
public sealed class EndpointFailoverRoutingConfigManaged
{
    /// <summary>
    /// CreateEndpoint with a failover RoutingConfig moves from CREATING to ACTIVE on its own,
    /// gets a server-assigned EndpointId and EndpointUrl, and DescribeEndpoint returns the exact
    /// FailoverConfig supplied at creation.
    /// </summary>
    [Test]
    [DisplayName("events.endpoint-failover-routing-config-managed")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var accountId = ctx.Account;

        // Route 53 is not emulated: the health check's ARN comes from FidelityContext.HealthCheckArn,
        // and its creation (a CALCULATED check) and cleanup are dropped.
        var healthCheckArn = FidelityContext.HealthCheckArn();

        var primaryRegion = ctx.Region;
        // Secondary.Route must name a region other than the primary bus's; either literal works.
        var secondaryRegion = primaryRegion != "us-west-2" ? "us-west-2" : "us-east-1";
        var primaryBusArn = $"arn:aws:events:{primaryRegion}:{accountId}:event-bus/default";
        var secondaryBusArn = $"arn:aws:events:{secondaryRegion}:{accountId}:event-bus/default";

        var endpointName = FidelityContext.UniqueName("sim2real-behav-ep-");
        var created = await events.CreateEndpointAsync(new CreateEndpointRequest
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
            EventBuses = [new EndpointEventBus { EventBusArn = primaryBusArn }, new EndpointEventBus { EventBusArn = secondaryBusArn }],
            ReplicationConfig = new ReplicationConfig { State = ReplicationState.DISABLED },
        });
        ctx.OnCleanup(() => events.DeleteEndpointAsync(new DeleteEndpointRequest { Name = endpointName }));

        Ensure(
            created.State?.Value is "CREATING" or "ACTIVE",
            $"expected CreateEndpoint's own State to be CREATING or ACTIVE, got '{created.State?.Value}'");

        var final = await PollUntilTerminal(events, endpointName, timeoutS: 90.0);
        Ensure(final.State?.Value == "ACTIVE", $"endpoint did not reach ACTIVE: State={final.State?.Value}");
        // server-managed identifiers were assigned, not supplied by the caller
        Ensure(!string.IsNullOrEmpty(final.EndpointId), $"expected a server-assigned EndpointId, got '{final.EndpointId}'");
        Ensure(!string.IsNullOrEmpty(final.EndpointUrl), $"expected a server-assigned EndpointUrl, got '{final.EndpointUrl}'");

        Ensure(
            final.RoutingConfig.FailoverConfig.Primary.HealthCheck == healthCheckArn,
            "Primary.HealthCheck drifted: sent " +
            $"'{healthCheckArn}' got '{final.RoutingConfig.FailoverConfig.Primary.HealthCheck}'");
        Ensure(
            final.RoutingConfig.FailoverConfig.Secondary.Route == secondaryRegion,
            "Secondary.Route drifted: sent " +
            $"'{secondaryRegion}' got '{final.RoutingConfig.FailoverConfig.Secondary.Route}'");
    }

    private static async Task<DescribeEndpointResponse> PollUntilTerminal(IAmazonEventBridge events, string endpointName, double timeoutS)
    {
        var clock = FidelityTime.StartNew();
        DescribeEndpointResponse? last = null;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            last = await events.DescribeEndpointAsync(new DescribeEndpointRequest { Name = endpointName });
            if (last.State?.Value != "CREATING")
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3.0));
        }

        return last ?? new DescribeEndpointResponse();
    }
}
