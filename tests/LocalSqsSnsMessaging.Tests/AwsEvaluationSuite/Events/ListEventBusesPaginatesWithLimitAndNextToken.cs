// Translated from behavioral/events/check_list_event_buses_paginates_with_limit_and_next_token.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.list-event-buses-paginates-with-limit-and-next-token</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListEventBusesPaginatesWithLimitAndNextToken
{
    private const string Check = "list-event-buses-paginates-with-limit-and-next-token";
    private const int Buses = 3;
    private const int MaxPages = 10;

    /// <summary>
    /// ListEventBuses with Limit=1 over three buses returns one bus per page and together each bus
    /// exactly once; Limit above the maximum of 100 and an unissued NextToken are both refused.
    /// </summary>
    [Test]
    [DisplayName("events.list-event-buses-paginates-with-limit-and-next-token")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var prefix = FidelityContext.UniqueName("sim2real-behav-pg-");
        var names = Enumerable.Range(0, Buses).Select(i => $"{prefix}-{i}").ToHashSet();
        foreach (var name in names.Order(StringComparer.Ordinal))
        {
            await events.CreateEventBusAsync(new CreateEventBusRequest { Name = name, Tags = [new Tag { Key = "sim2real", Value = Check }] });
            ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = name }));
        }

        var pages = new List<List<string>>();
        string? token = null;
        while (true)
        {
            var request = new ListEventBusesRequest { NamePrefix = prefix, Limit = 1 };
            if (!string.IsNullOrEmpty(token))
            {
                request.NextToken = token;
            }

            var resp = await events.ListEventBusesAsync(request);
            var page = (resp.EventBuses ?? []).Select(b => b.Name).ToList();
            Ensure(page.Count <= 1, $"Limit=1 returned a page of {page.Count} buses: {Repr(page)}");
            pages.Add(page);
            token = resp.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                break;
            }

            Ensure(
                pages.Count < MaxPages,
                $"ListEventBuses kept returning a NextToken after {pages.Count} pages for {Buses} buses: " +
                $"{Repr(pages)} — the token does not advance");
        }

        var listed = pages.SelectMany(p => p).ToList();
        Ensure(
            listed.Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal)),
            $"the pages together must return each of the {Buses} buses exactly once; got {Repr(listed)}");
        Ensure(
            pages.Count(p => p.Count > 0) == Buses,
            $"with Limit=1 the {Buses} buses must arrive on {Buses} separate pages, got {Repr(pages)}");

        // The SDK's analyzer refuses a constant over the model maximum; boto3 sends it, so this does too.
        var overMaximum = 101;
        var (code, message) = await Error(() => events.ListEventBusesAsync(new ListEventBusesRequest { NamePrefix = prefix, Limit = overMaximum }));
        Ensure(
            (code, message) == (
                "ValidationException",
                "1 validation error detected: Value '101' at 'limit' failed to satisfy constraint: " +
                "Member must have value less than or equal to 100"),
            $"Limit=101 must be refused, got '{code}' '{message}'");

        (code, message) = await Error(() => events.ListEventBusesAsync(new ListEventBusesRequest { NamePrefix = prefix, NextToken = "sim2real-not-a-token" }));
        Ensure(
            (code, message) == ("ValidationException", "Parameter NextToken is not valid."),
            $"a NextToken the service never issued must be refused, got '{code}' '{message}'");
    }

    /// <summary>(code, message) of a call that must fail; an unexpected success raises, naming the result.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message);
        }

        throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }

    private static string Repr(IEnumerable<string> items) => $"[{string.Join(", ", items.Select(i => $"'{i}'"))}]";

    private static string Repr(IEnumerable<List<string>> pages) => $"[{string.Join(", ", pages.Select(Repr))}]";
}
