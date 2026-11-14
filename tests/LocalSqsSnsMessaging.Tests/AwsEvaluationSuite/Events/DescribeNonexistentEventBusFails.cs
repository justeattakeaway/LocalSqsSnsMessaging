// Translated from behavioral/events/check_describe_nonexistent_event_bus_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.describe-nonexistent-event-bus-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class DescribeNonexistentEventBusFails
{
    /// <summary>
    /// DescribeEventBus on a bus that was never created raises ResourceNotFoundException,
    /// while a real bus keeps describing fine before and after the failed call.
    /// </summary>
    [Test]
    [DisplayName("events.describe-nonexistent-event-bus-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var name = FidelityContext.UniqueName("sim2real-behav-bus-");
        var resp = await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = name });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = name }));

        // positive control: the bus we just created describes successfully
        var described = await ev.DescribeEventBusAsync(new DescribeEventBusRequest { Name = name });
        Ensure(described.Name == name, "assert described[\"Name\"] == name");
        Ensure(described.Arn == resp.EventBusArn, "assert described[\"Arn\"] == resp[\"EventBusArn\"]");

        var bogus = FidelityContext.UniqueName("sim2real-behav-nobus-");
        var err = await Error(() => ev.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bogus }));
        Ensure(
            err == ("ResourceNotFoundException", $"Event bus {bogus} does not exist."),
            $"expected ResourceNotFoundException, got ('{err.Code}', '{err.Message}')");

        // effect unaffected: the real bus is still describable afterwards
        var describedAgain = await ev.DescribeEventBusAsync(new DescribeEventBusRequest { Name = name });
        Ensure(describedAgain.Name == name, "assert described_again[\"Name\"] == name");
    }

    /// <summary>(code, message) of a call that must fail; a call that succeeds raises AssertionError.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected ClientError, call unexpectedly succeeded");
        }

        return (error.Code, error.Message);
    }
}
