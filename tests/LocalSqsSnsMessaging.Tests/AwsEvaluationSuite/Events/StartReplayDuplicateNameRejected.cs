// Translated from behavioral/events/check_start_replay_duplicate_name_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.start-replay-duplicate-name-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class StartReplayDuplicateNameRejected
{
    private const string Check = "start-replay-duplicate-name-rejected";

    /// <summary>
    /// StartReplay with a ReplayName already in use fails with ResourceAlreadyExistsException and its
    /// exact message, leaves the original replay unchanged, and a fresh name on the same archive works.
    /// </summary>
    [Test]
    [DisplayName("events.start-replay-duplicate-name-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeArchives, "CreateArchive is not supported (NotSupportedException); AWS creates the archive the replays run against")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        var busName = FidelityContext.UniqueName("sim2real-eb-bus-");
        var archiveName = FidelityContext.UniqueName("sim2real-eb-arc-");
        var replayName = FidelityContext.UniqueName("sim2real-eb-replay-");
        var replayName2 = FidelityContext.UniqueName("sim2real-eb-replay2-");
        var sourceName = FidelityContext.UniqueName("sim2real.dup.src.").Replace('-', '.');

        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest
        {
            Name = busName,
            Tags = [new Tag { Key = "sim2real", Value = Check }],
        })).EventBusArn;
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName }));

        var archiveArn = (await events.CreateArchiveAsync(new CreateArchiveRequest
        {
            ArchiveName = archiveName,
            EventSourceArn = busArn,
            EventPattern = $$"""{"source": ["{{sourceName}}"]}""",
        })).ArchiveArn;
        ctx.OnCleanup(() => events.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = archiveName }));

        await PollUntil(
            async () => (await events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archiveName })).State?.Value,
            state => state == "ENABLED",
            timeoutS: 60,
            intervalS: 3);

        var now = FidelityTime.UtcNow.UtcDateTime;
        var originalStart = now - TimeSpan.FromMinutes(10);
        var originalEnd = now + TimeSpan.FromMinutes(10);

        var first = await events.StartReplayAsync(new StartReplayRequest
        {
            ReplayName = replayName,
            EventSourceArn = archiveArn,
            EventStartTime = originalStart,
            EventEndTime = originalEnd,
            Destination = new ReplayDestination { Arn = busArn },
        });
        Ensure(
            first.State?.Value is "STARTING" or "RUNNING" or "COMPLETED",
            $"positive control: expected StartReplay to acknowledge with a valid initial state, got {{'State': '{first.State?.Value}'}}");
        ctx.OnCleanup(() => BestEffortCancel(events, replayName));

        await PollUntil(
            async () => (await events.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = replayName })).State?.Value,
            state => state is "STARTING" or "RUNNING" or "COMPLETED",
            timeoutS: 30,
            intervalS: 2);

        // Reuse the name with a different time window.
        var conflictingEnd = now + TimeSpan.FromMinutes(20);
        var refusal = await ErrorOf(() => events.StartReplayAsync(new StartReplayRequest
        {
            ReplayName = replayName,
            EventSourceArn = archiveArn,
            EventStartTime = originalStart,
            EventEndTime = conflictingEnd,
            Destination = new ReplayDestination { Arn = busArn },
        }));
        Ensure(
            refusal == ("ResourceAlreadyExistsException", $"Replay {replayName} already exists."),
            $"expected the duplicate-name StartReplay to be rejected with ResourceAlreadyExistsException, got ('{refusal.Code}', '{refusal.Message}')");

        // EventEndTime is the one field the conflicting call tried to change, so it is the
        // assertion that discriminates.
        var described = await events.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = replayName });
        Ensure(
            described.EventSourceArn == archiveArn,
            $"expected the original replay's EventSourceArn to survive the rejected call unchanged, got {Show(described)}");
        Ensure(
            described.Destination?.Arn == busArn,
            $"expected the original replay's Destination to survive the rejected call unchanged, got {Show(described)}");
        var endDriftS = Math.Abs(((described.EventEndTime ?? throw new KeyNotFoundException("'EventEndTime'")) - originalEnd).TotalSeconds);
        Ensure(
            endDriftS < 2,
            $"expected EventEndTime to remain {originalEnd:O} (the rejected call asked for {conflictingEnd:O} instead), got {described.EventEndTime:O}");

        // A fresh name succeeds, so the rejection is specific to the name collision.
        var second = await events.StartReplayAsync(new StartReplayRequest
        {
            ReplayName = replayName2,
            EventSourceArn = archiveArn,
            EventStartTime = originalStart,
            EventEndTime = originalEnd,
            Destination = new ReplayDestination { Arn = busArn },
        });
        Ensure(
            second.State?.Value is "STARTING" or "RUNNING" or "COMPLETED",
            $"expected StartReplay with a fresh name to acknowledge with a valid initial state, got {{'State': '{second.State?.Value}'}}");
        ctx.OnCleanup(() => BestEffortCancel(events, replayName2));

        var described2 = await events.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = replayName2 });
        Ensure(
            described2.State?.Value is "STARTING" or "RUNNING" or "COMPLETED",
            $"expected an independent DescribeReplay read of the fresh-name replay to show it running, got {Show(described2)}");
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, double intervalS)
    {
        var started = FidelityTime.StartNew();
        T last = default!;
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"condition never met within {timeoutS}s, last value='{last}'");
    }

    /// <summary>Return fn()'s botocore (code, message); raise if the call unexpectedly succeeds.</summary>
    private static async Task<(string? Code, string? Message)> ErrorOf<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the duplicate-name StartReplay to be rejected, but it SUCCEEDED and returned {result}");
    }

    private static async Task BestEffortCancel(AmazonEventBridgeClient events, string replayName)
    {
        var error = await FidelityContext.CaptureError(async () =>
        {
            var state = (await events.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = replayName })).State?.Value;
            if (state is "STARTING" or "RUNNING")
            {
                await events.CancelReplayAsync(new CancelReplayRequest { ReplayName = replayName });
            }
        });
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }

    private static string Show(DescribeReplayResponse d) =>
        $"{{'ReplayName': '{d.ReplayName}', 'State': '{d.State?.Value}', 'EventSourceArn': '{d.EventSourceArn}', " +
        $"'Destination': {{'Arn': '{d.Destination?.Arn}'}}, 'EventEndTime': '{d.EventEndTime:O}'}}";
}
