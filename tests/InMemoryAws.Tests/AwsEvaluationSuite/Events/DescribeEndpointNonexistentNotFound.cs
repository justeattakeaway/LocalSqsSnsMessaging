// Translated from behavioral/events/check_describe_endpoint_nonexistent_not_found.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.describe-endpoint-nonexistent-not-found</summary>
[Category("AwsEvaluationSuite")]
public sealed class DescribeEndpointNonexistentNotFound
{
    /// <summary>
    /// DescribeEndpoint on an unknown endpoint name returns ResourceNotFoundException,
    /// while a real global endpoint keeps describing fine before and after the failed call.
    /// </summary>
    [Test]
    [DisplayName("events.describe-endpoint-nonexistent-not-found")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var name = await CreateEndpoint(ctx);

        // positive control: the endpoint we just created describes successfully
        var described = await events.DescribeEndpointAsync(new DescribeEndpointRequest { Name = name });
        Ensure(
            described.Name == name,
            $"positive control failed: expected DescribeEndpoint(Name='{name}') to return " +
            $"that same name, got '{described.Name}'");

        var bogus = FidelityContext.UniqueName("sim2real-behav-noendpoint-");
        var err = await Error(() => events.DescribeEndpointAsync(new DescribeEndpointRequest { Name = bogus }));
        Ensure(
            err == ("ResourceNotFoundException", $"Endpoint {bogus} does not exist in region {ctx.Region}."),
            $"expected DescribeEndpoint on unknown endpoint '{bogus}' to raise " +
            $"ResourceNotFoundException, got {err}");

        // effect absent / unaffected: the real endpoint is still describable afterwards
        var describedAgain = await events.DescribeEndpointAsync(new DescribeEndpointRequest { Name = name });
        Ensure(
            describedAgain.Name == name,
            $"real endpoint '{name}' should still describe fine after the unrelated " +
            $"not-found lookup, got Name='{describedAgain.Name}'");
    }

    /// <summary>
    /// Create a real global endpoint using only always-present default event buses and a
    /// real Route53 health check -- CreateEndpoint validates the health-check ARN against an
    /// actually-existing resource.
    /// </summary>
    /// <remarks>
    /// Route 53 is not emulated: the health check's ARN comes from
    /// <see cref="FidelityContext.HealthCheckArn"/>, and its creation and cleanup are dropped.
    /// </remarks>
    private static async Task<string> CreateEndpoint(FidelityContext ctx)
    {
        var events = ctx.Events;
        var accountId = ctx.Account;
        // A global endpoint's FailoverConfig requires two *distinct* regions; the literals
        // here are just "any region other than ctx.region", not a pin to a specific one.
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

    /// <summary>(code, message) of a call that must fail; a call that succeeds raises AssertionError.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected a ClientError, but the call SUCCEEDED");
        }

        return (error.Code, error.Message);
    }

    private static async Task PollUntil(Func<Task<bool>> predicate, double timeoutS, double intervalS)
    {
        var clock = FidelityTime.StartNew();
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            if (await predicate())
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        if (!await predicate())
        {
            throw new TimeoutException($"condition did not become true within {timeoutS}s");
        }
    }
}
