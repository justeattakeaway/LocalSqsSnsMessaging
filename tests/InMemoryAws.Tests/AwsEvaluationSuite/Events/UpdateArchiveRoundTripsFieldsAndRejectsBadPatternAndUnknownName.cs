// Translated from behavioral/events/check_update_archive_round_trips_fields_and_rejects_bad_pattern_and_unknown_name.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.update-archive-round-trips-fields-and-rejects-bad-pattern-and-unknown-name</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateArchiveRoundTripsFieldsAndRejectsBadPatternAndUnknownName
{
    private const string Check = "update-archive-round-trips-fields-and-rejects-bad-pattern-and-unknown-name";

    /// <summary>
    /// UpdateArchive's Description/RetentionDays change round-trips through DescribeArchive and
    /// ListArchives while EventSourceArn and EventPattern stay put; an invalid EventPattern is
    /// refused and leaves Description untouched; an unknown ArchiveName is refused by name.
    /// </summary>
    [Test]
    [DisplayName("events.update-archive-round-trips-fields-and-rejects-bad-pattern-and-unknown-name")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real", Value = Check }] })).EventBusArn;
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var archiveName = FidelityContext.UniqueName("sim2real-behav-archive-");
        const string originalJson = """{"source": ["sim2real.archive.original"]}""";
        await events.CreateArchiveAsync(new CreateArchiveRequest
        {
            ArchiveName = archiveName,
            EventSourceArn = busArn,
            Description = "initial description",
            EventPattern = originalJson,
            RetentionDays = 1,
        });
        ctx.OnCleanup(() => DeleteArchiveAndWait(events, archiveName));
        var created = await PollUntil(
            () => Describe(events, archiveName),
            d => d.State?.Value is "ENABLED" or "CREATE_FAILED" or "DISABLED",
            timeoutS: 300.0, intervalS: 3.0, description: $"archive {archiveName} leaving CREATING");
        Ensure(created.State?.Value == "ENABLED", $"archive settled at State='{created.State?.Value}'");

        // Change exactly Description and RetentionDays; EventPattern is omitted from the request.
        await events.UpdateArchiveAsync(new UpdateArchiveRequest { ArchiveName = archiveName, Description = "updated description", RetentionDays = 7 });
        var updated = await PollUntil(
            () => Describe(events, archiveName),
            d => d.RetentionDays == 7,
            timeoutS: 60.0, intervalS: 2.0, description: $"archive {archiveName} reflecting RetentionDays=7");
        Ensure(
            updated.Description == "updated description",
            $"expected Description 'updated description' after UpdateArchive, got '{updated.Description}'");
        Ensure(updated.RetentionDays == 7, $"expected RetentionDays 7 after UpdateArchive, got {updated.RetentionDays}");
        Ensure(
            updated.EventPattern == originalJson,
            $"UpdateArchive without an EventPattern must leave it at '{originalJson}', got '{updated.EventPattern}'");
        Ensure(
            updated.EventSourceArn == busArn,
            $"UpdateArchive must never change EventSourceArn; expected '{busArn}', got '{updated.EventSourceArn}'");

        var listed = await ListArchiveEntry(events, busArn, archiveName);
        Ensure(
            listed.RetentionDays == 7,
            $"ListArchives entry for '{archiveName}' shows RetentionDays={listed.RetentionDays}, expected 7 to match DescribeArchive");

        // Break exactly one thing: an unrecognised matcher operator inside an otherwise well-formed
        // pattern.
        const string badJson = """{"source": ["sim2real.archive.original"], "detail": {"n": [{"nosuchop": [1]}]}}""";
        await ExpectError(
            () => events.UpdateArchiveAsync(new UpdateArchiveRequest { ArchiveName = archiveName, EventPattern = badJson }),
            "InvalidEventPatternException",
            "Event pattern is not valid. Reason: Unrecognized match type nosuchop\n at [Source: " +
            $"(String)\"{badJson}\"; line: 1, column: {badJson.IndexOf("[1]", StringComparison.Ordinal) + 2}]");
        var afterBad = await Describe(events, archiveName);
        Ensure(
            afterBad.Description == "updated description",
            $"the refused UpdateArchive must leave Description at 'updated description', got '{afterBad.Description}'");
        Ensure(
            afterBad.EventPattern == originalJson,
            $"the refused UpdateArchive must leave EventPattern at '{originalJson}', got '{afterBad.EventPattern}'");
        Ensure(afterBad.RetentionDays == 7, $"the refused UpdateArchive must leave RetentionDays at 7, got {afterBad.RetentionDays}");

        // Positive control next to the refusal: the same construct corrected is accepted and takes
        // effect, proving the refusal above is specific to the bad operator.
        const string goodJson = """{"source": ["sim2real.archive.updated"]}""";
        await events.UpdateArchiveAsync(new UpdateArchiveRequest { ArchiveName = archiveName, EventPattern = goodJson });
        var afterGood = await PollUntil(
            () => Describe(events, archiveName),
            d => d.EventPattern == goodJson,
            timeoutS: 60.0, intervalS: 2.0, description: $"archive {archiveName} reflecting the corrected EventPattern");
        Ensure(
            afterGood.EventPattern == goodJson,
            $"expected EventPattern '{goodJson}' after the corrected UpdateArchive, got '{afterGood.EventPattern}'");

        // UpdateArchive on a name nothing ever created.
        var neverCreated = FidelityContext.UniqueName("sim2real-behav-archive-never-");
        await ExpectError(
            () => events.UpdateArchiveAsync(new UpdateArchiveRequest { ArchiveName = neverCreated, Description = "unreachable" }),
            "ResourceNotFoundException", $"Archive {neverCreated} does not exist.");

        // Positive control next to the refusal: the real archive is unaffected and still answers.
        var control = await Describe(events, archiveName);
        Ensure(
            control.EventPattern == goodJson,
            $"the real archive '{archiveName}' must still be readable and unaffected by the " +
            $"UpdateArchive on '{neverCreated}', got EventPattern='{control.EventPattern}'");
    }

    private static Task<DescribeArchiveResponse> Describe(IAmazonEventBridge events, string name) =>
        events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = name });

    /// <summary>Assert <c>fn()</c> raises exactly <c>(code, message)</c>; raise on an unexpected success.</summary>
    private static async Task ExpectError<T>(Func<Task<T>> fn, string code, string message)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected ('{code}', '{message}'), but the call succeeded: {result}");
        }

        Ensure(
            (error.Code, error.Message) == (code, message),
            $"expected ('{code}', '{message}'), got ('{error.Code}', '{error.Message}')");
    }

    /// <summary>The ListArchives entry named <paramref name="archiveName"/> under <paramref name="busArn"/>, following NextToken.</summary>
    private static async Task<Archive> ListArchiveEntry(IAmazonEventBridge events, string busArn, string archiveName)
    {
        string? token = null;
        while (true)
        {
            var request = new ListArchivesRequest { EventSourceArn = busArn };
            if (!string.IsNullOrEmpty(token))
            {
                request.NextToken = token;
            }

            var page = await events.ListArchivesAsync(request);
            foreach (var entry in page.Archives ?? [])
            {
                if (entry.ArchiveName == archiveName)
                {
                    return entry;
                }
            }

            token = page.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                throw Fail($"archive '{archiveName}' is absent from ListArchives over '{busArn}'");
            }
        }
    }

    /// <summary>
    /// Delete an archive and wait for it to be gone: the event bus cannot be deleted while an
    /// archive still references it.
    /// </summary>
    private static async Task DeleteArchiveAndWait(IAmazonEventBridge events, string name, double timeoutS = 180.0)
    {
        try
        {
            await events.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = name });
        }
        catch (Amazon.EventBridge.Model.ResourceNotFoundException)
        {
        }

        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            try
            {
                await Describe(events, name);
            }
            catch (Amazon.EventBridge.Model.ResourceNotFoundException)
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }

        throw Fail($"archive {name} still present {timeoutS}s after DeleteArchive");
    }

    /// <summary>Poll probe() until predicate(result) is truthy; return the final result (raises on timeout).</summary>
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

        throw Fail($"{description} not met within {timeoutS}s. Last result: {last}");
    }
}
