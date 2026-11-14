// Translated from behavioral/events/check_events_archive_replay_filter_arn_naming_a_rule_on_another_bus_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.events-archive-replay-filter-arn-naming-a-rule-on-another-bus-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsArchiveReplayFilterArnNamingARuleOnAnotherBusRejected
{
    /// <summary>
    /// StartReplay with Destination.FilterArns naming a rule on another bus is a ValidationException
    /// and creates no replay; the same request naming a rule on the destination bus is accepted.
    /// </summary>
    [Test]
    [DisplayName("events.events-archive-replay-filter-arn-naming-a-rule-on-another-bus-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var pattern = $$"""{"source": ["{{FidelityContext.UniqueName("sim2real.arr.src.").Replace('-', '.')}}"]}""";

        var sourceBus = FidelityContext.UniqueName("sim2real-arr-src-");
        var sourceArn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = sourceBus, Tags = TagsKv() })).EventBusArn;
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = sourceBus }));
        var otherBus = FidelityContext.UniqueName("sim2real-arr-oth-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = otherBus, Tags = TagsKv() });
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = otherBus }));

        var ownRule = FidelityContext.UniqueName("sim2real-arr-own-");
        var ownArn = (await events.PutRuleAsync(new PutRuleRequest { Name = ownRule, EventBusName = sourceBus, EventPattern = pattern, Tags = TagsKv() })).RuleArn;
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = ownRule, EventBusName = sourceBus }));
        var foreignRule = FidelityContext.UniqueName("sim2real-arr-for-");
        var foreignArn = (await events.PutRuleAsync(new PutRuleRequest { Name = foreignRule, EventBusName = otherBus, EventPattern = pattern, Tags = TagsKv() })).RuleArn;
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = foreignRule, EventBusName = otherBus }));

        var archive = FidelityContext.UniqueName("sim2real-arr-arc-");
        var archiveArn = (await events.CreateArchiveAsync(new CreateArchiveRequest { ArchiveName = archive, EventSourceArn = sourceArn })).ArchiveArn;
        ctx.OnCleanup(() => DeleteArchiveAndWait(events, archive));
        await WaitArchiveEnabled(events, archive);

        var now = FidelityTime.UtcNow.UtcDateTime;
        var windowStart = now - TimeSpan.FromMinutes(10);
        var windowEnd = now - TimeSpan.FromMinutes(1);

        // The destination bus is valid in both requests, isolating the rule-to-bus membership check.
        // StartReplay's model error list does not declare ValidationException.
        var refused = FidelityContext.UniqueName("sim2real-arr-rp-bad-");
        var err = await Error(() => events.StartReplayAsync(new StartReplayRequest
        {
            ReplayName = refused, EventSourceArn = archiveArn,
            Destination = new ReplayDestination { Arn = sourceArn, FilterArns = [foreignArn] },
            EventStartTime = windowStart, EventEndTime = windowEnd,
        }));
        var expected = (
            "ValidationException",
            "Parameter Destination.FilterArns is not valid. Reason: Rule must be on the destination " +
            "event bus.");
        Ensure(
            err == expected,
            $"StartReplay into '{sourceBus}' filtered to rule '{foreignArn}' (which lives on " +
            $"'{otherBus}') returned {Repr(err)}, expected {Repr(expected)}");
        var absent = await Error(() => events.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = refused }));
        Ensure(
            absent == ("ResourceNotFoundException", $"Replay {refused} does not exist."),
            $"the refused replay '{refused}' is describable afterwards (DescribeReplay -> {Repr(absent)})");
        var listed = await ListReplayNames(events, refused);
        Ensure(listed.Count == 0, $"the refused replay '{refused}' is listed: [{string.Join(", ", listed.Select(n => $"'{n}'"))}]");

        // Positive control: identical request, but the FilterArns rule is on the destination bus.
        var accepted = FidelityContext.UniqueName("sim2real-arr-rp-ok-");
        // No DeleteReplay API exists, so the accepted replay is not cleaned up.
        var ok = await events.StartReplayAsync(new StartReplayRequest
        {
            ReplayName = accepted, EventSourceArn = archiveArn,
            Destination = new ReplayDestination { Arn = sourceArn, FilterArns = [ownArn] },
            EventStartTime = windowStart, EventEndTime = windowEnd,
        });
        Ensure((ok.ReplayArn ?? "").EndsWith(accepted, StringComparison.Ordinal), $"control StartReplay returned {ok}");
        var desc = await events.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = accepted });
        Ensure(
            desc.Destination?.Arn == sourceArn && (desc.Destination.FilterArns ?? []).SequenceEqual([ownArn]),
            $"control replay's Destination is {desc.Destination}, expected Arn='{sourceArn}' " +
            $"FilterArns=['{ownArn}']");
    }

    private static List<Tag> TagsKv() => [new Tag { Key = "sim2real:managed", Value = "true" }];

    private static string Repr((string? Code, string? Message) e) => $"('{e.Code}', '{e.Message}')";

    /// <summary>A call expected to fail that succeeds raises (the Python's <c>_Accepted</c>), so it can never read as an error code.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw new InvalidOperationException($"call was expected to be refused but succeeded: {result}");
        }

        return (error.Code ?? "<no code>", error.Message);
    }

    private static async Task<List<string>> ListReplayNames(IAmazonEventBridge events, string prefix)
    {
        var names = new List<string>();
        string? token = null;
        while (true)
        {
            var page = await events.ListReplaysAsync(new ListReplaysRequest { NamePrefix = prefix, NextToken = token });
            names.AddRange((page.Replays ?? []).Select(r => r.ReplayName));
            if (string.IsNullOrEmpty(page.NextToken))
            {
                return names;
            }

            token = page.NextToken;
        }
    }

    private static async Task WaitArchiveEnabled(IAmazonEventBridge events, string name, double timeoutS = 300.0)
    {
        var deadline = FidelityTime.StartNew();
        string? state = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            state = (await events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = name })).State?.Value;
            if (state is "ENABLED" or "CREATE_FAILED" or "DISABLED")
            {
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        Ensure(state == "ENABLED", $"archive {name} settled at State='{state}', expected 'ENABLED'");
    }

    /// <summary>The bus cannot be deleted while an archive still references it, so wait for the delete.</summary>
    private static async Task DeleteArchiveAndWait(IAmazonEventBridge events, string name, double timeoutS = 180.0)
    {
        var error = await FidelityContext.CaptureError(() => events.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = name }));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw new InvalidOperationException($"could not delete archive {name}: {error}", error.Exception);
        }

        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var describeError = await FidelityContext.CaptureError(() => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = name }));
            if (describeError?.Code == "ResourceNotFoundException")
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw new InvalidOperationException($"archive {name} still present {timeoutS:0.0}s after DeleteArchive");
    }
}
