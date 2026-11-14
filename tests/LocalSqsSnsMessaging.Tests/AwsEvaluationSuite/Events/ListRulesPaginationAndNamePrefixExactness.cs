// Translated from behavioral/events/check_list_rules_pagination_and_name_prefix_exactness.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.list-rules-pagination-and-name-prefix-exactness</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListRulesPaginationAndNamePrefixExactness
{
    /// <summary>
    /// ListRules paginates without drops, duplicates or a trailing NextToken, and NamePrefix is an
    /// anchored, case-sensitive prefix filter that composes with pagination.
    ///
    /// Reference: https://docs.aws.amazon.com/eventbridge/latest/APIReference/API_ListRules.html
    /// </summary>
    [Test]
    [DisplayName("events.list-rules-pagination-and-name-prefix-exactness")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeListing, "ListRules ignores Limit: Limit=5 over twelve rules returns all twelve on one page")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        // A brand-new custom bus makes the collection closed, so results are compared by equality.
        var ev = ctx.Events;
        List<Tag> tags = [new Tag { Key = "sim2real:managed", Value = "true" }];

        // Registered before the rules so the LIFO cleanup order deletes the rules first: an event bus
        // that still has rules on it cannot be deleted.
        var bus = FidelityContext.UniqueName("sim2real-behav-");
        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = tags });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var @base = FidelityContext.UniqueName("sim2real-behav-");
        const string pattern = """{"source": ["sim2real.orders"]}""";

        var alpha = Enumerable.Range(0, 8).Select(i => $"{@base}-alpha{i:00}").ToList();
        var beta = Enumerable.Range(0, 4).Select(i => $"{@base}-beta{i:00}").ToList();
        var created = alpha.Concat(beta).ToList();
        foreach (var name in created)
        {
            await ev.PutRuleAsync(new PutRuleRequest { Name = name, EventPattern = pattern, State = RuleState.DISABLED, EventBusName = bus, Tags = tags });
            ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = name, EventBusName = bus }));
        }

        foreach (var name in created)
        {
            Ensure(name.Length <= 64, $"rule name '{name}' is {name.Length} chars, over the 64-char maximum");
        }

        var everything = await ev.ListRulesAsync(new ListRulesRequest { EventBusName = bus });
        Ensure(
            Sorted(RulesOf(everything).Select(r => r.Name)).SequenceEqual(Sorted(created)),
            $"a freshly created bus must contain exactly the rules this check created; got " +
            $"{Repr(Sorted(RulesOf(everything).Select(r => r.Name)))}");
        Ensure(
            everything.NextToken is null,
            $"an unpaged ListRules that returned all {created.Count} rules must not carry a NextToken, " +
            $"got '{everything.NextToken}'");

        foreach (var (limit, minPages) in new[] { (5, 3), (1, created.Count) })
        {
            var pages = await Pages(ev, bus, limit: limit);
            var names = pages.SelectMany(p => RulesOf(p).Select(r => r.Name)).ToList();
            Ensure(
                pages.Count >= minPages,
                $"Limit={limit} over {created.Count} rules must take at least {minPages} pages, " +
                $"took {pages.Count}");
            for (var i = 0; i < pages.Count; i++)
            {
                Ensure(
                    RulesOf(pages[i]).Count <= limit,
                    $"page {i} of a Limit={limit} walk returned {RulesOf(pages[i]).Count} rules, " +
                    $"more than the requested limit");
            }

            for (var i = 0; i < pages.Count - 1; i++)
            {
                Ensure(
                    !string.IsNullOrEmpty(pages[i].NextToken),
                    $"page {i} of {pages.Count} is not the last page, so it must carry a NextToken");
            }

            Ensure(
                pages[^1].NextToken is null,
                $"the LAST page of a Limit={limit} walk must not carry a NextToken, got " +
                $"'{pages[^1].NextToken}'; an always-present NextToken makes every caller's " +
                $"pagination loop non-terminating");
            Ensure(
                names.Count == names.Distinct().Count(),
                $"Limit={limit} walk returned duplicates: " +
                $"{Repr(Sorted(names.Distinct().Where(n => names.Count(x => x == n) > 1)))}");
            Ensure(
                Sorted(names).SequenceEqual(Sorted(created)),
                $"Limit={limit} walk lost or invented rules. missing=" +
                $"{Repr(Sorted(created.Except(names)))} extra={Repr(Sorted(names.Except(created)))}");
        }

        var gotAlpha = Sorted(RulesOf(await ev.ListRulesAsync(new ListRulesRequest { EventBusName = bus, NamePrefix = $"{@base}-alpha" })).Select(r => r.Name));
        Ensure(gotAlpha.SequenceEqual(Sorted(alpha)), $"NamePrefix must return exactly the 8 alpha rules, got {Repr(gotAlpha)}");
        var gotBeta = Sorted(RulesOf(await ev.ListRulesAsync(new ListRulesRequest { EventBusName = bus, NamePrefix = $"{@base}-beta" })).Select(r => r.Name));
        Ensure(gotBeta.SequenceEqual(Sorted(beta)), $"NamePrefix must return exactly the 4 beta rules, got {Repr(gotBeta)}");

        // Adding an unrelated rule must not disturb the alpha result.
        var contains = $"{@base}-zz-alpha-suffix";
        await ev.PutRuleAsync(new PutRuleRequest { Name = contains, EventPattern = pattern, State = RuleState.DISABLED, EventBusName = bus, Tags = tags });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = contains, EventBusName = bus }));
        var after = Sorted(RulesOf(await ev.ListRulesAsync(new ListRulesRequest { EventBusName = bus, NamePrefix = $"{@base}-alpha" })).Select(r => r.Name));
        Ensure(after.SequenceEqual(Sorted(alpha)), $"adding '{contains}' must not change the {@base}-alpha result, got {Repr(after)}");
        Ensure(
            RulesOf(await ev.ListRulesAsync(new ListRulesRequest { EventBusName = bus })).Any(r => r.Name == contains),
            $"control failed: '{contains}' must appear in the UNfiltered listing");

        // Anchored, not a substring search: `zz-alpha` occurs mid-name in `contains` and starts no
        // rule, so a prefix filter returns nothing while a substring filter would return it.
        var mid = Sorted(RulesOf(await ev.ListRulesAsync(new ListRulesRequest { EventBusName = bus, NamePrefix = "zz-alpha" })).Select(r => r.Name));
        Ensure(
            mid.Count == 0,
            $"NamePrefix is anchored at the start, not a substring search: 'zz-alpha' occurs only in " +
            $"the MIDDLE of '{contains}' and begins no rule name, so the filtered result must be " +
            $"empty, got {Repr(mid)}");

        var upper = RulesOf(await ev.ListRulesAsync(new ListRulesRequest { EventBusName = bus, NamePrefix = $"{@base}-ALPHA" }));
        Ensure(
            upper.Count == 0,
            $"NamePrefix must be case-sensitive; the upper-cased prefix matched " +
            $"{Repr(upper.Select(r => r.Name))}");

        // A prefix that matches nothing: empty list, and no NextToken to chase.
        var none = await ev.ListRulesAsync(new ListRulesRequest { EventBusName = bus, NamePrefix = $"{@base}-gamma" });
        Ensure(
            RulesOf(none).Count == 0,
            $"a NamePrefix matching nothing must return an empty Rules list, got " +
            $"{Repr(RulesOf(none).Select(r => r.Name))}");
        Ensure(none.NextToken is null, $"an empty filtered result must not carry a NextToken, got '{none.NextToken}'");

        var filteredPages = await Pages(ev, bus, namePrefix: $"{@base}-alpha", limit: 3);
        var filteredNames = filteredPages.SelectMany(p => RulesOf(p).Select(r => r.Name)).ToList();
        Ensure(
            Sorted(filteredNames).SequenceEqual(Sorted(alpha)),
            $"paging a FILTERED listing must reconstruct only the filtered set; got {Repr(Sorted(filteredNames))}");
        Ensure(filteredNames.Count == filteredNames.Distinct().Count(), $"paged filtered walk returned duplicates: {Repr(filteredNames)}");
    }

    /// <summary>
    /// Walk ListRules to exhaustion, returning the raw pages; bounded by max_pages so an
    /// always-present NextToken fails instead of looping forever.
    /// </summary>
    private static async Task<List<ListRulesResponse>> Pages(
        AmazonEventBridgeClient ev, string bus, string? namePrefix = null, int? limit = null, int maxPages = 200)
    {
        var pages = new List<ListRulesResponse>();
        string? token = null;
        while (true)
        {
            var call = new ListRulesRequest { EventBusName = bus, NamePrefix = namePrefix, Limit = limit };
            if (token is not null)
            {
                call.NextToken = token;
            }

            var page = await ev.ListRulesAsync(call);
            pages.Add(page);
            token = page.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                return pages;
            }

            Ensure(
                pages.Count < maxPages,
                $"ListRules still returned a NextToken after {pages.Count} pages over a collection of " +
                $"at most a few dozen rules; the cursor never terminates");
        }
    }

    /// <summary><c>resp["Rules"]</c>: a missing key raises KeyError, as in Python.</summary>
    private static List<Rule> RulesOf(ListRulesResponse resp) => resp.Rules ?? throw new KeyNotFoundException("'Rules'");

    private static List<string> Sorted(IEnumerable<string> items) => [.. items.Order(StringComparer.Ordinal)];

    private static string Repr(IEnumerable<string> items) => $"[{string.Join(", ", items.Select(i => $"'{i}'"))}]";
}
