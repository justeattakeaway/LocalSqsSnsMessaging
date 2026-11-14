// Translated from behavioral/events/check_create_archive_duplicate_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.create-archive-duplicate-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateArchiveDuplicateFails
{
    /// <summary>
    /// CreateArchive with an existing ArchiveName is rejected with ResourceAlreadyExistsException,
    /// leaves the existing archive's Description untouched, and succeeds under a fresh name.
    /// </summary>
    [Test]
    [DisplayName("events.create-archive-duplicate-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var busResp = await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = Tags() });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var archiveName = FidelityContext.UniqueName("sim2real-behav-archive-");
        await ev.CreateArchiveAsync(new CreateArchiveRequest
        {
            ArchiveName = archiveName,
            EventSourceArn = busResp.EventBusArn,
            Description = "original",
        });
        ctx.OnCleanup(() => ev.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = archiveName }));
        await AwaitArchiveEnabled(ev, archiveName);

        var err = await Error(() => ev.CreateArchiveAsync(new CreateArchiveRequest
        {
            ArchiveName = archiveName,
            EventSourceArn = busResp.EventBusArn,
            Description = "duplicate-attempt-should-not-apply",
        }));
        var expected = ("ResourceAlreadyExistsException", $"Archive {archiveName} already exists.");
        Ensure(
            err == expected,
            $"expected duplicate CreateArchive to fail with {Repr(expected)}, got {Repr(err)}");

        // Effect-absence: the rejected duplicate request must not have mutated the existing archive.
        var after = await ev.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archiveName });
        Ensure(
            after.Description == "original",
            "rejected duplicate CreateArchive mutated the existing archive's Description, got " +
            $"'{after.Description}'");

        // Positive control: the identical shape of request succeeds with a fresh, non-conflicting name.
        var controlName = FidelityContext.UniqueName("sim2real-behav-archive-ctrl-");
        await ev.CreateArchiveAsync(new CreateArchiveRequest
        {
            ArchiveName = controlName,
            EventSourceArn = busResp.EventBusArn,
            Description = "control",
        });
        ctx.OnCleanup(() => ev.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = controlName }));
        var controlAfter = await AwaitArchiveEnabled(ev, controlName);
        Ensure(
            controlAfter.Description == "control",
            "control CreateArchive with a fresh name did not apply, got " +
            $"'{controlAfter.Description}'");
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

    private static async Task<DescribeArchiveResponse> AwaitArchiveEnabled(IAmazonEventBridge ev, string archiveName, double timeoutS = 30)
    {
        var deadline = FidelityTime.StartNew();
        DescribeArchiveResponse? desc = null;
        var settled = false;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            desc = await ev.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archiveName });
            if (desc.State?.Value is "ENABLED" or "CREATE_FAILED")
            {
                settled = true;
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        if (!settled)
        {
            throw Fail($"archive {archiveName} did not settle within {timeoutS}s: {desc}");
        }

        Ensure(desc!.State?.Value == "ENABLED", $"archive {archiveName} did not become ENABLED: {desc}");
        return desc;
    }

    private static List<Tag> Tags() => [new Tag { Key = "sim2real:managed", Value = "true" }];

    private static string Repr((string? Code, string? Message) e) => $"('{e.Code}', '{e.Message}')";
}
