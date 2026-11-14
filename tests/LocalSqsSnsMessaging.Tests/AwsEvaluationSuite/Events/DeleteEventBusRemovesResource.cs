// Translated from behavioral/events/check_delete_event_bus_removes_resource.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.delete-event-bus-removes-resource</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteEventBusRemovesResource
{
    private const string Check = "delete-event-bus-removes-resource";

    /// <summary>
    /// DeleteEventBus removes the bus: an independent DescribeEventBus 404s, a
    /// PutRule targeting the dead id is rejected too, and the freed name is
    /// reusable for a fresh CreateEventBus.
    /// </summary>
    [Test]
    [DisplayName("events.delete-event-bus-removes-resource")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var name = FidelityContext.UniqueName("sim2real-behav-bus-");
        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = name, Tags = [new Tag { Key = "sim2real-check", Value = Check }] });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = name }));
        await ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = name });

        var expected = ("ResourceNotFoundException", $"Event bus {name} does not exist.");
        var describeErr = await Err(() => ev.DescribeEventBusAsync(new DescribeEventBusRequest { Name = name }));
        Ensure(
            describeErr == expected,
            $"expected DescribeEventBus to fail with {Repr(expected)} after delete, got {Repr(describeErr)}");

        var rule = FidelityContext.UniqueName("sim2real-behav-rule-");
        var putRuleErr = await Err(() => ev.PutRuleAsync(new PutRuleRequest { Name = rule, EventBusName = name }));
        Ensure(
            putRuleErr == expected,
            $"expected PutRule against the deleted bus to be rejected with {Repr(expected)}, got {Repr(putRuleErr)}");

        var resp = await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = name, Tags = [new Tag { Key = "sim2real-check", Value = Check }] });
        Ensure(!string.IsNullOrEmpty(resp.EventBusArn), "the freed bus name must be reusable for a fresh CreateEventBus");
        var desc = await ev.DescribeEventBusAsync(new DescribeEventBusRequest { Name = name });
        Ensure(desc.Name == name, $"recreated bus must describe under the same name, got {desc.Name}");
    }

    /// <summary>Call fn(); return its (code, message). An unexpected SUCCESS raises.</summary>
    private static async Task<(string? Code, string? Message)> Err<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, but the call SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }

    private static string Repr((string? Code, string? Message) e) => $"('{e.Code}', '{e.Message}')";
}
