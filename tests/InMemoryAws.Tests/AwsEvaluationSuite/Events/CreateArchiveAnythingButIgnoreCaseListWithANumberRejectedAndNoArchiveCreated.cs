// Translated from behavioral/events/check_create_archive_anything_but_ignore_case_list_with_a_number_rejected_and_no_archive_created.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.create-archive-anything-but-ignore-case-list-with-a-number-rejected-and-no-archive-created</summary>
[Category("AwsEvaluationSuite")]
public sealed class CreateArchiveAnythingButIgnoreCaseListWithANumberRejectedAndNoArchiveCreated
{
    private static List<Tag> Tags() => [new Tag { Key = "sim2real:managed", Value = "true" }];

    /// <summary>
    /// CreateArchive refuses an anything-but equals-ignore-case list holding a number with its exact
    /// message and creates no archive, while the all-string neighbour list creates one storing it.
    /// </summary>
    [Test]
    [DisplayName("events.create-archive-anything-but-ignore-case-list-with-a-number-rejected-and-no-archive-created")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = Tags() })).EventBusArn;
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        var source = FidelityContext.UniqueName("sim2real.eic.");

        var badName = FidelityContext.UniqueName("sim2real-behav-arch-");
        var bad = PyDumps(new JsonObject
        {
            ["detail"] = JsonNode.Parse("""{"k": [{"anything-but": {"equals-ignore-case": ["a", 1]}}]}"""),
            ["source"] = new JsonArray(source),
        });
        var (code, msg) = await Error(() => events.CreateArchiveAsync(new CreateArchiveRequest
        {
            ArchiveName = badName, EventSourceArn = busArn, EventPattern = bad,
        }));
        ctx.OnCleanup(() => events.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = badName }));
        var column = bad.IndexOf("\"a\", 1", StringComparison.Ordinal) + "\"a\", 1".Length + 1;
        var at = $" at [Source: (String)\"{bad}\"; line: 1, column: {column}]";
        var want = "Event pattern is not valid. Reason: Inside anything-but/equals-ignore-case list, " +
            $"number|start|null|boolean is not supported.\n{at}\n{at}";
        Ensure(code == "InvalidEventPatternException", $"CreateArchive raised '{code}': '{msg}'");
        Ensure(msg == want, $"refusal message '{msg}' != '{want}'");

        var (goneCode, goneMsg) = await Error(() => events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = badName }));
        Ensure(
            (goneCode, goneMsg) == ("ResourceNotFoundException", $"Archive {badName} does not exist."),
            $"the refused CreateArchive left an archive behind: '{goneCode}' '{goneMsg}'");

        var goodName = FidelityContext.UniqueName("sim2real-behav-arch-");
        var good = PyDumps(new JsonObject
        {
            ["detail"] = JsonNode.Parse("""{"k": [{"anything-but": {"equals-ignore-case": ["a", "1"]}}]}"""),
            ["source"] = new JsonArray(source),
        });
        await events.CreateArchiveAsync(new CreateArchiveRequest { ArchiveName = goodName, EventSourceArn = busArn, EventPattern = good });
        ctx.OnCleanup(() => events.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = goodName }));
        var stored = await events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = goodName });
        Ensure(stored.EventPattern == good, $"stored pattern '{stored.EventPattern}' != '{good}'");
        Ensure(stored.EventSourceArn == busArn, $"archive source '{stored.EventSourceArn}' is not the check's bus");
    }

    /// <summary>Run `call`; return the (code, message) it raised. Raises if it SUCCEEDED.</summary>
    private static async Task<(string Code, string Message)> Error<T>(Func<Task<T>> call)
        where T : class
    {
        var (_, error) = await FidelityContext.Capture(call);
        if (error is null)
        {
            throw Fail("the call succeeded where AWS refuses it");
        }

        return (error.Code ?? "<missing Error.Code>", error.Message ?? "");
    }

    /// <summary><c>json.dumps</c>: ", " and ": " separators; numbers keep the text they were parsed from.</summary>
    private static string PyDumps(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(", ", o.Select(kv => $"{JsonSerializer.Serialize(kv.Key)}: {PyDumps(kv.Value)}")) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(PyDumps)) + "]",
        _ => node.ToJsonString(),
    };
}
