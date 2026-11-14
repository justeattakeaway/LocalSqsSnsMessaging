// Translated from behavioral/events/check_delete_default_event_bus_protected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.delete-default-event-bus-protected</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteDefaultEventBusProtected
{
    private const string Check = "delete-default-event-bus-protected";

    /// <summary>DeleteEventBus works on a real bus but is rejected against "default".</summary>
    [Test]
    [DisplayName("events.delete-default-event-bus-protected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        // Positive control: DeleteEventBus actually deletes buses in this account, so the rejection
        // below is specific to "default" and not evidence of a target where delete never works.
        var controlName = FidelityContext.UniqueName("sim2real-behav-bus-");
        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = controlName, Tags = [new Tag { Key = "sim2real", Value = Check }] });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = controlName }));
        await ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = controlName });
        var (controlCode, controlDetail) = await ErrorCode(() => ev.DescribeEventBusAsync(new DescribeEventBusRequest { Name = controlName }));
        Ensure(
            controlCode == "ResourceNotFoundException",
            $"positive control: expected the custom bus gone after delete, got '{controlCode}' " +
            $"('{controlDetail}')");

        var (code, message) = await ErrorCode(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = "default" }));
        Ensure(
            code == "ValidationException",
            $"expected ValidationException deleting the default event bus, got '{code}' ('{message}')");
        Ensure(
            message == "Cannot delete event bus default.",
            $"expected the specific default-bus refusal message, got '{message}'");

        var desc = await ev.DescribeEventBusAsync(new DescribeEventBusRequest { Name = "default" });
        Ensure(
            desc.Name == "default",
            $"default event bus must survive a delete attempt, got '{desc.Name}'");
    }

    /// <summary>
    /// (code, message) for a call that fails; a sentinel that cannot be mistaken for a real
    /// code, plus the response, if the call unexpectedly succeeds.
    /// </summary>
    private static async Task<(string? Code, string? Message)> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (resp, error) = await FidelityContext.Capture(fn);
        return error is null ? ("<call unexpectedly SUCCEEDED>", resp?.ToString()) : (error.Code, error.Message);
    }
}
