// Translated from behavioral/events/check_delete_event_bus_blocked_while_an_archive_targets_it.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.delete-event-bus-blocked-while-an-archive-targets-it</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteEventBusBlockedWhileAnArchiveTargetsIt
{
    private const double ArchiveEnabledTimeoutS = 90.0;
    private const double ArchiveGoneTimeoutS = 180.0;
    private const double BusGoneTimeoutS = 60.0;

    // The one code real EventBridge returns for "this bus still has a rule" (archives included).
    private const string ExpectedBlockCode = "ValidationException";

    private const string Prefix = "sim2real-behav-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// An event bus with an attached archive cannot be deleted (ValidationException, "has rules"),
    /// and the identical DeleteEventBus succeeds once the archive is deleted.
    /// </summary>
    /// <remarks>
    /// Reference: https://docs.aws.amazon.com/eventbridge/latest/APIReference/API_DeleteEventBus.html
    /// </remarks>
    [Test]
    [DisplayName("events.delete-event-bus-blocked-while-an-archive-targets-it")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeArchives, "CreateArchive is not supported (NotSupportedException)")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        // Positive control: DeleteEventBus works on a bus with nothing attached.
        var controlBus = BusName();
        var controlArn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = controlBus, Tags = TagsKv() })).EventBusArn;
        ctx.OnCleanup(() => DeleteBusQuietly(ctx, controlBus));
        Ensure(
            controlArn.EndsWith($"/{controlBus}", StringComparison.Ordinal),
            $"CreateEventBus must return an ARN ending in /{controlBus}, got '{controlArn}'");
        await events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = controlBus });
        var controlCode = await PollUntil(
            () => ErrorCode(() => events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = controlBus })),
            c => c == "ResourceNotFoundException",
            timeoutS: BusGoneTimeoutS,
            intervalS: 2.0,
            description: $"control bus {controlBus} disappears after DeleteEventBus");
        Ensure(
            controlCode == "ResourceNotFoundException",
            $"positive control failed: after DeleteEventBus({controlBus}) DescribeEventBus must return " +
            $"ResourceNotFoundException, got '{controlCode}' — DeleteEventBus is not working in this " +
            "account/region, so the refusal asserted below would prove nothing");

        var bus = BusName();
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = TagsKv() })).EventBusArn;
        ctx.OnCleanup(() => DeleteBusQuietly(ctx, bus));

        var archive = ArchiveName();
        await events.CreateArchiveAsync(new CreateArchiveRequest
        {
            ArchiveName = archive,
            EventSourceArn = busArn,
            Description = "sim2real delete-bus-blocked-by-archive",
            EventPattern = """{"source": [{"prefix": "sim2real."}]}""",
            RetentionDays = 1,
        });
        ctx.OnCleanup(() => DeleteArchiveAndWait(ctx, archive));
        var enabled = await PollUntil(
            () => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archive }),
            d => d.State?.Value is "ENABLED" or "CREATE_FAILED",
            timeoutS: ArchiveEnabledTimeoutS,
            intervalS: 2.0,
            description: $"archive {archive} leaves CREATING");
        Ensure(
            enabled.State?.Value == "ENABLED",
            $"archive {archive} must reach ENABLED before the dependency can be tested, got " +
            $"State='{enabled.State?.Value}' StateReason='{enabled.StateReason}'");
        Ensure(
            enabled.EventSourceArn == busArn,
            $"the archive must report the bus it was created from: expected EventSourceArn='{busArn}', " +
            $"got '{enabled.EventSourceArn}'");

        var (code, message, status) = await Error(
            () => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }),
            $"DeleteEventBus({bus}) while archive {archive} still referenced it (an unconditional " +
            "delete orphans the archive; this is the over-permissive failure the check exists to " +
            "catch)");
        Ensure(
            code == ExpectedBlockCode,
            $"expected the archive dependency refusal to be '{ExpectedBlockCode}', got '{code}' " +
            $"with message '{message}'");
        // The message is the generic "has rules" refusal: CreateArchive provisions a hidden managed rule
        // (Events-Archive-<name>) on the source bus, and that rule is what DeleteEventBus detects.
        Ensure(status == 400, $"the archive dependency refusal carried HTTP {status}, expected 400");
        Ensure(!string.IsNullOrEmpty(message), $"the archive dependency refusal carried an empty message (code='{code}')");
        var rulesOnBus = (await events.ListRulesAsync(new ListRulesRequest { EventBusName = bus })).Rules ?? [];
        var managedRuleName = $"Events-Archive-{archive}";
        var managedRule = rulesOnBus.FirstOrDefault(r => r.Name == managedRuleName);
        Ensure(
            managedRule is not null,
            $"CreateArchive({archive}) must provision a managed rule named '{managedRuleName}' on " +
            $"'{bus}' — that hidden rule is the actual dependency DeleteEventBus refuses to break; " +
            $"ListRules(EventBusName='{bus}') returned [{string.Join(", ", rulesOnBus.Select(r => $"'{r.Name}'"))}]");
        Ensure(
            !string.IsNullOrEmpty(managedRule.ManagedBy),
            "the archive's hidden rule must be reported as service-managed (non-empty ManagedBy), " +
            $"got rule {managedRule}");

        var stillThere = await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus });
        Ensure(
            stillThere.Arn == busArn,
            $"the refused delete must leave the bus untouched: expected Arn='{busArn}', got " +
            $"'{stillThere.Arn}'");
        var listed = await BusNames(events, bus);
        Ensure(
            listed.Contains(bus),
            $"the refused delete must leave the bus in ListEventBuses(NamePrefix='{bus}'); got {PyList(listed)}");
        var archiveAfter = await events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archive });
        Ensure(
            archiveAfter.State?.Value == "ENABLED",
            $"the refused delete must leave the archive ENABLED, got '{archiveAfter.State?.Value}'");

        // Deleting the archive must also remove its managed rule.
        await events.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = archive });
        var goneCode = await PollUntil(
            () => ErrorCode(() => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archive })),
            c => c == "ResourceNotFoundException",
            timeoutS: ArchiveGoneTimeoutS,
            intervalS: 3.0,
            description: $"archive {archive} disappears after DeleteArchive");
        Ensure(
            goneCode == "ResourceNotFoundException",
            $"DescribeArchive({archive}) must return ResourceNotFoundException once the archive is " +
            $"deleted, got '{goneCode}'");
        var rulesAfterRestore = ((await events.ListRulesAsync(new ListRulesRequest { EventBusName = bus })).Rules ?? []).Select(r => r.Name).ToList();
        Ensure(
            !rulesAfterRestore.Contains(managedRuleName),
            $"deleting the archive must remove its hidden managed rule '{managedRuleName}' from " +
            $"'{bus}' too; ListRules(EventBusName='{bus}') still returned {PyList(rulesAfterRestore)}");

        await events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus });
        var finalCode = await PollUntil(
            () => ErrorCode(() => events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus })),
            c => c == "ResourceNotFoundException",
            timeoutS: BusGoneTimeoutS,
            intervalS: 2.0,
            description: $"bus {bus} disappears once its archive is gone");
        Ensure(
            finalCode == "ResourceNotFoundException",
            $"after the archive was removed the identical DeleteEventBus({bus}) must succeed and leave " +
            $"DescribeEventBus returning ResourceNotFoundException, got '{finalCode}'");
        var (_, finalMessage, finalStatus) = await Error(
            () => events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus }), $"DescribeEventBus({bus}) after delete");
        Ensure(finalStatus == 400, $"DescribeEventBus on the deleted bus carried HTTP {finalStatus}, expected 400");
        Ensure(
            finalMessage.Contains(bus, StringComparison.Ordinal),
            $"DescribeEventBus on the deleted bus must name '{bus}', got '{finalMessage}'");
        Ensure(
            (await BusNames(events, bus)).Count == 0,
            $"the deleted bus must be absent from ListEventBuses(NamePrefix='{bus}'), got " +
            $"{PyList(await BusNames(events, bus))}");
        // Cleanup is registered before the call so an unexpected success does not leak the archive.
        var orphanArchive = ArchiveName();
        ctx.OnCleanup(() => DeleteArchiveAndWait(ctx, orphanArchive));
        var (mutateCode, mutateMessage, mutateStatus) = await Error(
            () => events.CreateArchiveAsync(new CreateArchiveRequest
            {
                ArchiveName = orphanArchive, EventSourceArn = busArn, RetentionDays = 1,
            }),
            $"CreateArchive against the deleted bus {busArn}");
        Ensure(
            mutateCode == "ResourceNotFoundException",
            $"CreateArchive against the deleted bus '{busArn}' must fail with " +
            $"ResourceNotFoundException, got '{mutateCode}'");
        Ensure(mutateStatus == 400, $"CreateArchive on the deleted bus carried HTTP {mutateStatus}, expected 400");
        var expectedMessage = $"Event bus {bus} does not exist.";
        Ensure(mutateMessage == expectedMessage, $"expected the message '{expectedMessage}', got '{mutateMessage}'");
    }

    private static List<Tag> TagsKv() =>
    [
        new Tag { Key = ManagedTagKey, Value = "true" },
        new Tag { Key = "sim2real:prefix", Value = Prefix.TrimEnd('-') },
    ];

    private static string BusName() => FidelityContext.UniqueName($"{Prefix}bus-");

    /// <summary>ArchiveName is capped at 48 characters by real EventBridge, so the kind token stays short.</summary>
    private static string ArchiveName()
    {
        var name = FidelityContext.UniqueName($"{Prefix}ar-");
        return name.Length > 48 ? name[..48] : name;
    }

    /// <summary>(error code, message, HTTP status) for a call expected to fail; a success raises.</summary>
    private static async Task<(string? Code, string Message, int Status)> Error<T>(Func<Task<T>> fn, string what = "the call")
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"{what} SUCCEEDED where real EventBridge refuses it: {result}");
        }

        return (error.Code, error.Message ?? "", (int)error.StatusCode);
    }

    /// <summary>A poll probe: the error code fn() raises, or "present" while the read still succeeds.</summary>
    private static async Task<string?> ErrorCode(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        return error is null ? "present" : error.Code;
    }

    /// <summary>Poll until predicate holds; RAISE on timeout (never return the last value).</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, double intervalS, string description)
    {
        var deadline = FidelityTime.StartNew();
        T? last = default;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not met within {timeoutS:0.0}s. Last result: {last}");
    }

    private static async Task<List<string>> BusNames(Amazon.EventBridge.IAmazonEventBridge events, string prefix) =>
        [.. ((await events.ListEventBusesAsync(new ListEventBusesRequest { NamePrefix = prefix })).EventBuses ?? []).Select(b => b.Name)];

    /// <summary>
    /// Teardown: delete the archive and wait, because the bus teardown depends on it.
    /// Only ResourceNotFoundException is tolerated; any other failure raises.
    /// </summary>
    private static async Task DeleteArchiveAndWait(FidelityContext ctx, string name)
    {
        var events = ctx.Events;
        var error = await FidelityContext.CaptureError(() => events.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = name }));
        if (error is not null && error.Exception is not Amazon.EventBridge.Model.ResourceNotFoundException)
        {
            throw new FidelityAssertionException($"could not delete archive {name}: {error.Code}", error.Exception);
        }

        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(ArchiveGoneTimeoutS))
        {
            var describeError = await FidelityContext.CaptureError(() => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = name }));
            if (describeError?.Exception is Amazon.EventBridge.Model.ResourceNotFoundException)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"archive {name} still present {ArchiveGoneTimeoutS:0.0}s after DeleteArchive");
    }

    /// <summary>
    /// Teardown: the bus may already be gone (the body deletes two of them on the happy path);
    /// only ResourceNotFoundException is tolerated, any other failure raises.
    /// </summary>
    private static async Task DeleteBusQuietly(FidelityContext ctx, string name)
    {
        var error = await FidelityContext.CaptureError(() => ctx.Events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = name }));
        if (error is not null && error.Exception is not Amazon.EventBridge.Model.ResourceNotFoundException)
        {
            throw new FidelityAssertionException($"could not delete event bus {name}: {error.Code}", error.Exception);
        }
    }

    private static string PyList(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";
}
