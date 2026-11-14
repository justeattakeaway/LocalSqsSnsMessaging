// Translated from behavioral/events/check_create_archive_nonexistent_bus_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.create-archive-nonexistent-bus-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateArchiveNonexistentBusFails
{
    /// <summary>
    /// CreateArchive on a nonexistent bus fails ResourceNotFoundException and creates nothing; the
    /// identical call succeeds once that bus exists, and an archive on a real bus reaches ENABLED.
    /// </summary>
    [Test]
    [DisplayName("events.create-archive-nonexistent-bus-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeArchives, "CreateArchive is not supported (NotSupportedException)")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        var controlBusName = FidelityContext.UniqueName("sim2real-behav-bus-ctl-");
        var controlBusArn = (await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = controlBusName })).EventBusArn;
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = controlBusName }));
        var controlArchiveName = FidelityContext.UniqueName("sim2real-behav-archive-ctl-");
        await ev.CreateArchiveAsync(new CreateArchiveRequest { ArchiveName = controlArchiveName, EventSourceArn = controlBusArn });
        ctx.OnCleanup(() => ev.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = controlArchiveName }));
        var controlDesc = await PollArchiveSettled(ev, controlArchiveName);
        Ensure(
            controlDesc.State?.Value == "ENABLED",
            "control archive against a real bus did not reach ENABLED, so the refusal below would " +
            $"be indistinguishable from CreateArchive rejecting everything: {controlDesc}");

        var accountId = controlBusArn.Split(':')[4];
        var targetBusName = FidelityContext.UniqueName("sim2real-behav-bus-tgt-");
        var fakeBusArn = $"arn:aws:events:{ctx.Region}:{accountId}:event-bus/{targetBusName}";
        var archiveName = FidelityContext.UniqueName("sim2real-behav-archive-");

        var err = await Error(() => ev.CreateArchiveAsync(new CreateArchiveRequest { ArchiveName = archiveName, EventSourceArn = fakeBusArn }));
        var expected = ("ResourceNotFoundException", $"Event bus {targetBusName} does not exist.");
        Ensure(
            err == expected,
            $"CreateArchive against nonexistent bus {fakeBusArn} expected {Repr(expected)}, got {Repr(err)}");

        var absent = await Error(() => ev.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archiveName }));
        var expectedAbsent = ("ResourceNotFoundException", $"Archive {archiveName} does not exist.");
        Ensure(
            absent == expectedAbsent,
            "the refused CreateArchive still left something DescribeArchive can see: independent " +
            $"read of {archiveName} settled on {Repr(absent)} instead of {Repr(expectedAbsent)}");

        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = targetBusName });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = targetBusName }));
        await ev.CreateArchiveAsync(new CreateArchiveRequest { ArchiveName = archiveName, EventSourceArn = fakeBusArn });
        ctx.OnCleanup(() => ev.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = archiveName }));
        var restoredDesc = await PollArchiveSettled(ev, archiveName);
        Ensure(
            restoredDesc.State?.Value == "ENABLED",
            $"the identical CreateArchive call should succeed once {targetBusName} exists, but the " +
            $"archive settled at State='{restoredDesc.State?.Value}': {restoredDesc}");
    }

    /// <summary>Call fn() and return its (code, message); an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, but the call SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }

    private static async Task<DescribeArchiveResponse> PollArchiveSettled(IAmazonEventBridge ev, string archiveName, double timeoutS = 60.0)
    {
        var deadline = FidelityTime.StartNew();
        DescribeArchiveResponse? desc = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            desc = await ev.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archiveName });
            if (desc.State?.Value is "ENABLED" or "CREATE_FAILED")
            {
                return desc;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        throw Fail($"archive {archiveName} did not settle within {timeoutS}s: {desc}");
    }

    private static string Repr((string? Code, string? Message) e) => $"('{e.Code}', '{e.Message}')";
}
