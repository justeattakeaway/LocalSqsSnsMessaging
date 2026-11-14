// Translated from behavioral/events/check_create_event_bus_basic.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.create-event-bus-basic</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateEventBusBasic
{
    /// <summary>CreateEventBus creates a bus immediately visible via DescribeEventBus.</summary>
    [Test]
    [DisplayName("events.create-event-bus-basic")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var name = FidelityContext.UniqueName("sim2real-behav-bus-");
        var resp = await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = name, Tags = [new Tag { Key = "sim2real", Value = "1" }] });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = name }));
        Ensure(!string.IsNullOrEmpty(resp.EventBusArn), "CreateEventBus must return EventBusArn");
        var desc = await ev.DescribeEventBusAsync(new DescribeEventBusRequest { Name = name });
        Ensure(desc.Name == name, $"DescribeEventBus Name must echo '{name}', got '{desc.Name}'");
        Ensure(
            desc.Arn == resp.EventBusArn,
            "DescribeEventBus Arn must match CreateEventBus's EventBusArn " +
            $"'{resp.EventBusArn}', got '{desc.Arn}'");
    }
}
