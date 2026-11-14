// Translated from behavioral/events/check_update_event_bus_partial_update_agrees_across_every_read_surface.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.Runtime;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using EventBridgeTag = Amazon.EventBridge.Model.Tag;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.update-event-bus-partial-update-agrees-across-every-read-surface</summary>
[Category("AwsEvaluationSuite")]
public sealed class UpdateEventBusPartialUpdateAgreesAcrossEveryReadSurface
{
    private const int MaxDescription = 512; // model constant: EventBusDescription @length(max: 512)

    // The subset of DescribeEventBusResponse that UpdateEventBusResponse also carries. Policy,
    // CreationTime and LastModifiedTime are excluded because UpdateEventBus does not return them.
    private static readonly string[] SharedFields = ["Name", "Arn", "Description", "KmsKeyIdentifier", "DeadLetterConfig", "LogConfig"];

    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// A partial UpdateEventBus preserves the field it does not name and agrees with
    /// DescribeEventBus and ListEventBuses; LogConfig needs both sub-fields; the 512-char Description
    /// limit holds on create and update.
    /// </summary>
    [Test]
    [DisplayName("events.update-event-bus-partial-update-agrees-across-every-read-surface")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeEventBuses, "DescribeEventBus does not echo the LogConfig set by CreateEventBus (LogConfig is None instead of {'IncludeDetail': 'FULL', 'Level': 'TRACE'})")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var firstDescription = $"sim2real desc {Guid.NewGuid():N}";
        const string logConfig = "{'IncludeDetail': 'FULL', 'Level': 'TRACE'}";

        // No CloudWatch Logs destination is needed for CreateEventBus to accept LogConfig.
        var created = await events.CreateEventBusAsync(new CreateEventBusRequest
        {
            Name = bus,
            Description = firstDescription,
            LogConfig = new LogConfig { IncludeDetail = IncludeDetail.FULL, Level = Level.TRACE },
            Tags = TagsKv(),
        });
        ctx.OnCleanup(() => DeleteBusQuietly(ctx, bus));
        Ensure(
            created.EventBusArn.EndsWith($"/{bus}", StringComparison.Ordinal),
            $"CreateEventBus must return an ARN ending in /{bus}, got '{created.EventBusArn}'");

        // --- positive control + representation: both authored fields come back through a different op. ---
        var described = await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus });
        Ensure(
            described.Description == firstDescription,
            "DescribeEventBus must echo the Description set at create: expected " +
            $"'{firstDescription}', got {Quote(described.Description)}");
        Ensure(
            Repr(described.LogConfig) == logConfig,
            $"DescribeEventBus must echo the LogConfig set at create: expected {logConfig}, got " +
            $"{Repr(described.LogConfig)} (H1 — if CreateEventBus refused LogConfig outright, the " +
            "refusal is the contract and this check must be re-shaped to assert it)");

        // --- the partial update: name ONLY Description, then hold the two surfaces to each other. ---
        var secondDescription = $"sim2real desc {Guid.NewGuid():N}";
        var updated = await events.UpdateEventBusAsync(new UpdateEventBusRequest { Name = bus, Description = secondDescription });
        var reread = await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus });
        Ensure(
            reread.Description == secondDescription,
            $"the field the update named must change: expected Description='{secondDescription}', " +
            $"DescribeEventBus reports {Quote(reread.Description)}");
        AssertSurfacesAgree(Fields(updated), Fields(reread), label: "Description-only UpdateEventBus");
        Ensure(
            Repr(reread.LogConfig) == logConfig,
            "a partial UpdateEventBus naming only Description must PRESERVE LogConfig untouched " +
            $"(H2 measured: preserve, not clear): expected {logConfig}, DescribeEventBus reports " +
            $"{Repr(reread.LogConfig)}");

        // --- ListEventBuses is a third, independent surface and must agree on Description. ---
        var rows = ((await events.ListEventBusesAsync(new ListEventBusesRequest { NamePrefix = bus })).EventBuses ?? []).Where(b => b.Name == bus).ToList();
        if (rows.Count != 1)
        {
            var names = ((await events.ListEventBusesAsync(new ListEventBusesRequest { NamePrefix = bus })).EventBuses ?? []).Select(b => $"'{b.Name}'");
            throw Fail(
                $"ListEventBuses(NamePrefix='{bus}') must return exactly one row for the bus, got " +
                $"[{string.Join(", ", names)}]");
        }

        Ensure(
            rows[0].Description == secondDescription,
            "the ListEventBuses row must carry the same Description as DescribeEventBus: expected " +
            $"'{secondDescription}', got {Quote(rows[0].Description)}");
        Ensure(
            rows[0].Arn == reread.Arn,
            "the ListEventBuses row and DescribeEventBus must report the same ARN: " +
            $"{Quote(rows[0].Arn)} vs '{reread.Arn}'");

        // --- LogConfig is an atomic struct on UpdateEventBus, not a field-by-field patch:
        // naming it at all requires BOTH sub-fields; omitting either is refused and leaves it untouched. ---
        var missingLevel = await Error(
            () => events.UpdateEventBusAsync(new UpdateEventBusRequest { Name = bus, LogConfig = new LogConfig { IncludeDetail = IncludeDetail.NONE } }),
            "UpdateEventBus with LogConfig missing Level");
        Ensure(
            missingLevel.Code == "ValidationException" && missingLevel.Message.Contains("Level", StringComparison.Ordinal),
            "UpdateEventBus with LogConfig missing Level must be refused naming the missing field: " +
            $"got ('{missingLevel.Code}', '{missingLevel.Message}')");
        var missingIncludeDetail = await Error(
            () => events.UpdateEventBusAsync(new UpdateEventBusRequest { Name = bus, LogConfig = new LogConfig { Level = Level.OFF } }),
            "UpdateEventBus with LogConfig missing IncludeDetail");
        Ensure(
            missingIncludeDetail.Code == "ValidationException" && missingIncludeDetail.Message.Contains("IncludeDetail", StringComparison.Ordinal),
            "UpdateEventBus with LogConfig missing IncludeDetail must be refused naming the missing " +
            $"field: got ('{missingIncludeDetail.Code}', '{missingIncludeDetail.Message}')");
        var untouched = await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus });
        Ensure(
            Repr(untouched.LogConfig) == logConfig,
            "both refused, partially-specified LogConfig updates must leave the bus's LogConfig " +
            $"untouched: expected {logConfig}, DescribeEventBus reports {Repr(untouched.LogConfig)}");

        // --- the mirror-image partial update: name ONLY LogConfig, with both sub-fields the service
        // requires — Description must survive untouched, the same way LogConfig survived above. ---
        var logOnly = await events.UpdateEventBusAsync(new UpdateEventBusRequest
        {
            Name = bus, LogConfig = new LogConfig { IncludeDetail = IncludeDetail.NONE, Level = Level.OFF },
        });
        var afterLog = await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus });
        Ensure(
            afterLog.LogConfig?.Level?.Value == "OFF",
            "the field the update named must change: expected LogConfig.Level='OFF', " +
            $"DescribeEventBus reports {Repr(afterLog.LogConfig)} (H5)");
        Ensure(
            afterLog.Description == secondDescription,
            "a partial UpdateEventBus naming only LogConfig must PRESERVE Description untouched: " +
            $"expected '{secondDescription}', DescribeEventBus reports {Quote(afterLog.Description)}");
        AssertSurfacesAgree(Fields(logOnly), Fields(afterLog), label: "LogConfig-only UpdateEventBus");

        // --- the Description length boundary, on both write paths, with the effect proven absent. ---
        var atMax = new string('d', MaxDescription);
        await events.UpdateEventBusAsync(new UpdateEventBusRequest { Name = bus, Description = atMax });
        if ((await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus })).Description != atMax)
        {
            throw Fail(
                $"a Description of exactly the documented maximum {MaxDescription} characters must be " +
                "stored verbatim; DescribeEventBus reports " +
                $"{((await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus })).Description ?? "").Length} characters");
        }

        // botocore does not validate string maxima, so the over-length value reaches the service.
        var updateRefusal = await Error(
            () => events.UpdateEventBusAsync(new UpdateEventBusRequest { Name = bus, Description = new string('d', MaxDescription + 1) }),
            $"UpdateEventBus with a {MaxDescription + 1}-character Description (the documented " +
            $"{MaxDescription}-character limit must be enforced by the service)");
        if ((await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus })).Description != atMax)
        {
            throw Fail(
                $"the refused over-length update must not have written: expected the {MaxDescription}" +
                "-character Description to survive, got " +
                $"{Quote((await events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = bus })).Description)}");
        }

        var overLongBus = FidelityContext.UniqueName("sim2real-behav-bus-");
        ctx.OnCleanup(() => DeleteBusQuietly(ctx, overLongBus));
        var createRefusal = await Error(
            () => events.CreateEventBusAsync(new CreateEventBusRequest
            {
                Name = overLongBus, Description = new string('d', MaxDescription + 1), Tags = TagsKv(),
            }),
            $"CreateEventBus with a {MaxDescription + 1}-character Description (both write paths " +
            "must enforce the same documented limit)");
        Ensure(
            createRefusal.Code == updateRefusal.Code,
            "the create and update paths must refuse an over-length Description with the same error " +
            $"code: create gave '{createRefusal.Code}', update gave '{updateRefusal.Code}' (H4)");
        var absent = await Error(
            () => events.DescribeEventBusAsync(new DescribeEventBusRequest { Name = overLongBus }),
            $"DescribeEventBus of the refused bus {overLongBus}");
        Ensure(
            absent.Code == "ResourceNotFoundException",
            "the refused CreateEventBus must not have created a bus: " +
            $"DescribeEventBus({overLongBus}) returned '{absent.Code}'");
    }

    private static List<EventBridgeTag> TagsKv() =>
    [
        new EventBridgeTag { Key = ManagedTagKey, Value = "true" },
        new EventBridgeTag { Key = "sim2real:prefix", Value = "sim2real-behav" },
    ];

    private static string Quote(string? value) => value is null ? "None" : $"'{value}'";

    /// <summary>Python's repr of the LogConfig dict botocore returns, or None.</summary>
    private static string Repr(LogConfig? config)
    {
        if (config is null)
        {
            return "None";
        }

        var parts = new List<string>();
        if (config.IncludeDetail is not null)
        {
            parts.Add($"'IncludeDetail': '{config.IncludeDetail.Value}'");
        }

        if (config.Level is not null)
        {
            parts.Add($"'Level': '{config.Level.Value}'");
        }

        return "{" + string.Join(", ", parts) + "}";
    }

    private static string Repr(DeadLetterConfig? config) => config is null ? "None" : $"{{'Arn': {Quote(config.Arn)}}}";

    /// <summary><c>{f: response.get(f) for f in _SHARED_FIELDS}</c>, each rendered as Python would print it.</summary>
    private static Dictionary<string, string> Fields(UpdateEventBusResponse r) => new(StringComparer.Ordinal)
    {
        ["Name"] = Quote(r.Name),
        ["Arn"] = Quote(r.Arn),
        ["Description"] = Quote(r.Description),
        ["KmsKeyIdentifier"] = Quote(r.KmsKeyIdentifier),
        ["DeadLetterConfig"] = Repr(r.DeadLetterConfig),
        ["LogConfig"] = Repr(r.LogConfig),
    };

    private static Dictionary<string, string> Fields(DescribeEventBusResponse r) => new(StringComparer.Ordinal)
    {
        ["Name"] = Quote(r.Name),
        ["Arn"] = Quote(r.Arn),
        ["Description"] = Quote(r.Description),
        ["KmsKeyIdentifier"] = Quote(r.KmsKeyIdentifier),
        ["DeadLetterConfig"] = Repr(r.DeadLetterConfig),
        ["LogConfig"] = Repr(r.LogConfig),
    };

    /// <summary>
    /// Every field UpdateEventBus and DescribeEventBus have in common must carry the same value.
    /// </summary>
    /// <remarks>Callers pair it with a direct assertion on the updated field, since two empty surfaces agree.</remarks>
    private static void AssertSurfacesAgree(Dictionary<string, string> mutated, Dictionary<string, string> read, string label)
    {
        var disagreements = SharedFields.Where(f => mutated[f] != read[f]).ToList();
        if (disagreements.Count > 0)
        {
            var pairs = "{" + string.Join(", ", disagreements.Select(f => $"\"{f}\": [{mutated[f]}, {read[f]}]")) + "}";
            throw Fail(
                $"{label}: the mutating call and the independent read disagree on " +
                $"[{string.Join(", ", disagreements.Order(StringComparer.Ordinal).Select(f => $"'{f}'"))}] — (UpdateEventBus, DescribeEventBus) pairs were " +
                $"{pairs[..Math.Min(300, pairs.Length)]}");
        }
    }

    /// <summary>(code, message) for a call expected to fail; a success raises, naming what.</summary>
    private static async Task<(string? Code, string Message)> Error<T>(Func<Task<T>> fn, string what = "the call")
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"{what} SUCCEEDED where real EventBridge refuses it: {result!.GetType().Name}");
        }

        return (error.Code, error.Message ?? "");
    }

    /// <summary>Teardown; the bus may never have been created (one leg deliberately fails to create it).</summary>
    private static async Task DeleteBusQuietly(FidelityContext ctx, string name)
    {
        try
        {
            await ctx.Events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = name });
        }
        catch (AmazonServiceException exc) when (exc.ErrorCode == "ResourceNotFoundException")
        {
        }
    }
}
