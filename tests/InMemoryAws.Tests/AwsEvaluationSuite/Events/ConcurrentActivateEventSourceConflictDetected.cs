// Translated from behavioral/events/check_concurrent_activate_event_source_conflict_detected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.concurrent-activate-event-source-conflict-detected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentActivateEventSourceConflictDetected
{
    /// <summary>
    /// A non-partner CreatePartnerEventSource is refused and ten concurrent ActivateEventSource
    /// calls all fail ResourceNotFoundException; if the source exists, a race must hit a conflict.
    /// </summary>
    [Test]
    [DisplayName("events.concurrent-activate-event-source-conflict-detected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var account = ctx.Account;

        // Positive control: ordinary (non-partner) control-plane operations work with these
        // credentials, so what follows is a targeted partner-privilege guard, not a blanket deny.
        var busName = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName });
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName })));
        var describedBus = await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = busName });
        Ensure(
            describedBus.Name == busName,
            $"expected DescribeEventBus to reflect the created bus '{busName}', got " +
            $"'{describedBus.Name}'");

        var partnerSegment = FidelityContext.UniqueName("sim2real-behav-partner-");
        var sourceName = $"aws.partner/{partnerSegment}/testnamespace/testevent";

        var createError = await FidelityContext.CaptureError(() =>
            events.CreatePartnerEventSourceAsync(new CreatePartnerEventSourceRequest { Name = sourceName, Account = account }));
        const int racerCount = 10;
        if (createError is not null)
        {
            (string?, string?) createErr = (createError.Code, createError.Message);
            var ns = sourceName[..sourceName.LastIndexOf('/', sourceName.LastIndexOf('/') - 1)];
            (string?, string?) expectedCreate = (
                "ValidationException",
                $"Caller is not whitelisted for the event source partner namespace {ns}");
            Ensure(
                createErr == expectedCreate,
                $"expected the APN-partner-namespace rejection {expectedCreate}, got {createErr}");

            var calls = Enumerable.Range(0, racerCount)
                .Select(_ => (Func<Task>)(() => events.ActivateEventSourceAsync(new ActivateEventSourceRequest { Name = sourceName })))
                .ToList();
            var errors = await FireConcurrent(ctx, calls);
            (string?, string?) expectedRacer = (
                "ResourceNotFoundException",
                $"Event source {sourceName} or event bus {sourceName} does not exist.");
            Ensure(
                errors.All(error => error == expectedRacer),
                "expected every concurrent Activate call against a never-created partner source to " +
                $"observe {expectedRacer} uniformly, errors=[{string.Join(", ", errors)}]");

            var describeErr = await Error(() => events.DescribeEventSourceAsync(new DescribeEventSourceRequest { Name = sourceName }));
            (string?, string?) expectedDescribe = (
                "ResourceNotFoundException",
                $"Event source {sourceName} does not exist.");
            Ensure(
                describeErr == expectedDescribe,
                "the concurrent Activate calls must not have fabricated a describable source, got " +
                $"{describeErr}");
            return;
        }

        ctx.OnCleanup(() => IgnoreMissing(() =>
            events.DeletePartnerEventSourceAsync(new DeletePartnerEventSourceRequest { Name = sourceName, Account = account })));

        var racers = Enumerable.Range(0, racerCount)
            .Select(_ => (Func<Task>)(() => events.ActivateEventSourceAsync(new ActivateEventSourceRequest { Name = sourceName })))
            .ToList();
        var codes = (await FireConcurrent(ctx, racers)).Select(error => error.Code).ToList();

        Ensure(codes.Any(code => code is null), "expected at least one Activate call to succeed");
        Ensure(
            codes.Any(code => code == "ConcurrentModificationException"),
            $"no ConcurrentModificationException observed among {racerCount} concurrent " +
            $"ActivateEventSource calls, codes=[{string.Join(", ", codes)}]");
    }

    /// <summary>(code, message) per call; (None, None) for a call that succeeded.</summary>
    private static async Task<List<(string? Code, string? Message)>> FireConcurrent(FidelityContext ctx, List<Func<Task>> fns)
    {
        using var _ = ctx.Concurrently();
        var futures = fns.Select(fn => Task.Run(async () =>
        {
            var error = await FidelityContext.CaptureError(fn);
            return (error?.Code, error?.Message);
        })).ToList();
        return [.. await Task.WhenAll(futures)];
    }

    /// <summary>Call fn() and return its (code, message); an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected a ClientError, but the call SUCCEEDED");
        }

        return (error.Code, error.Message);
    }

    private static async Task IgnoreMissing(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null && error.Code is not ("ResourceNotFoundException" or "ValidationException"))
        {
            throw error.Exception;
        }
    }

    private static async Task IgnoreNotFound(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }
}
