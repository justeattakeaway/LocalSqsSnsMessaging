// Translated from behavioral/events/check_endpoint_routing_config_round_trip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.endpoint-routing-config-round-trip</summary>
[Category("AwsEvaluationSuite")]
public sealed class EndpointRoutingConfigRoundTrip
{
    /// <summary>
    /// The exact health-check ARN, secondary-region string, and the two EventBuses ARNs
    /// (in the order supplied) survive CreateEndpoint and come back byte-identical from
    /// DescribeEndpoint -- no re-casing, re-ordering, or ARN-shape normalization.
    /// </summary>
    [Test]
    [DisplayName("events.endpoint-routing-config-round-trip")]
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
        // A real secondary AWS Region for the failover config -- not a resource-region hardcode.
        var secondaryRegion = primaryRegion != "us-west-2" ? "us-west-2" : "us-east-1";

        // Mixed-case, punctuated name to make "byte-identical" a meaningful assertion.
        var endpointName = FidelityContext.UniqueName("Sim2Real-Behav.Endpoint_");
        var primaryBusArn = $"arn:aws:events:{primaryRegion}:{accountId}:event-bus/default";
        var secondaryBusArn = $"arn:aws:events:{secondaryRegion}:{accountId}:event-bus/default";
        string[] inputEventBuses = [primaryBusArn, secondaryBusArn];

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
            EventBuses = [.. inputEventBuses.Select(arn => new EndpointEventBus { EventBusArn = arn })],
            ReplicationConfig = new ReplicationConfig { State = ReplicationState.DISABLED },
        });
        ctx.OnCleanup(() => events.DeleteEndpointAsync(new DeleteEndpointRequest { Name = endpointName }));

        var final = await PollUntilTerminal(events, endpointName, timeoutS: 90.0);

        // The endpoint must have actually come up -- otherwise a target that fails creation but
        // still echoes the requested config back would pass this check for the wrong reason.
        Ensure(final.State?.Value == "ACTIVE", $"endpoint did not reach ACTIVE: State={final.State?.Value}");
        Ensure(final.Name == endpointName, "assert final[\"Name\"] == endpoint_name");
        var gotEventBuses = (final.EventBuses ?? []).Select(b => b.EventBusArn).ToList();
        Ensure(
            gotEventBuses.SequenceEqual(inputEventBuses),
            $"EventBuses drifted: sent [{string.Join(", ", inputEventBuses)}] got [{string.Join(", ", gotEventBuses)}]");
        var failover = final.RoutingConfig?.FailoverConfig;
        Ensure(
            failover?.Primary?.HealthCheck == healthCheckArn && failover?.Secondary?.Route == secondaryRegion,
            $"RoutingConfig drifted: sent ({healthCheckArn}, {secondaryRegion}) got ({failover?.Primary?.HealthCheck}, {failover?.Secondary?.Route})");

        // Confirm no case-folding of the region string tucked inside Secondary.Route.
        Ensure(
            final.RoutingConfig!.FailoverConfig.Secondary.Route == secondaryRegion,
            "assert final[\"RoutingConfig\"][\"FailoverConfig\"][\"Secondary\"][\"Route\"] == secondary_region");
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

        throw new TimeoutException($"endpoint '{endpointName}' still CREATING after {timeoutS}s: last State={last?.State?.Value}");
    }
}
