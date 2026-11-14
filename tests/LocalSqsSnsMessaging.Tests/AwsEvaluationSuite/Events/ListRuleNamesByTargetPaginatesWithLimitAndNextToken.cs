// Translated from behavioral/events/check_list_rule_names_by_target_paginates_with_limit_and_next_token.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.list-rule-names-by-target-paginates-with-limit-and-next-token</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListRuleNamesByTargetPaginatesWithLimitAndNextToken
{
    /// <summary>
    /// ListRuleNamesByTarget pages ten rules on a fresh bus by Limit without drops or duplicates,
    /// ending with no NextToken, and a later RemoveTargets shrinks the paged result by exactly that
    /// rule.
    /// </summary>
    [Test]
    [DisplayName("events.list-rule-names-by-target-paginates-with-limit-and-next-token")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        List<Tag> tag = [new Tag { Key = "sim2real:managed", Value = "true" }];

        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = tag });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var @base = FidelityContext.UniqueName("sim2real-behav-rule-");
        const string pattern = """{"source": ["sim2real.lrnbt"]}""";
        var targetArn = $"arn:aws:lambda:{ctx.Region}:{ctx.Account}:function:{FidelityContext.UniqueName("sim2real-behav-fn-")}";

        var names = Enumerable.Range(0, 10).Select(i => $"{@base}-{i:00}").ToList();
        foreach (var name in names)
        {
            await ev.PutRuleAsync(new PutRuleRequest { Name = name, EventPattern = pattern, State = RuleState.DISABLED, EventBusName = bus, Tags = tag });
            ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = name, EventBusName = bus, Force = true }));
            await ev.PutTargetsAsync(new PutTargetsRequest { Rule = name, EventBusName = bus, Targets = [new Target { Id = "t1", Arn = targetArn }] });
            ctx.OnCleanup(() => ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, EventBusName = bus, Ids = ["t1"], Force = true }));
        }

        // --- unpaged: the closed collection is exactly what was created, no NextToken. -------------
        var everything = await ev.ListRuleNamesByTargetAsync(new ListRuleNamesByTargetRequest { TargetArn = targetArn, EventBusName = bus });
        var everythingNames = everything.RuleNames ?? throw new KeyNotFoundException("'RuleNames'");
        Ensure(
            Sorted(everythingNames).SequenceEqual(Sorted(names)),
            $"ListRuleNamesByTarget with no Limit must return exactly the 10 rules pointed at " +
            $"'{targetArn}'; got {Repr(Sorted(everythingNames))}");
        Ensure(
            everything.NextToken is null,
            $"an unpaged call that returned all {names.Count} rules must not carry a NextToken, got " +
            $"'{everything.NextToken}'");

        // --- paging with Limit=3, then Limit=1 -----------------------------------------------------
        foreach (var (limit, minPages) in new[] { (3, 4), (1, names.Count) })
        {
            var pages = await Pages(ev, targetArn, bus, limit);
            var got = pages.SelectMany(p => p.RuleNames ?? throw new KeyNotFoundException("'RuleNames'")).ToList();
            Ensure(
                pages.Count >= minPages,
                $"Limit={limit} over {names.Count} rules must take at least {minPages} pages, took " +
                $"{pages.Count}");
            for (var i = 0; i < pages.Count; i++)
            {
                Ensure(
                    pages[i].RuleNames!.Count <= limit,
                    $"page {i} of a Limit={limit} walk returned {pages[i].RuleNames!.Count} names, more " +
                    $"than the requested limit");
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
                got.Count == got.Distinct().Count(),
                $"Limit={limit} walk returned duplicates: " +
                $"{Repr(Sorted(got.Distinct().Where(n => got.Count(x => x == n) > 1)))}");
            Ensure(
                Sorted(got).SequenceEqual(Sorted(names)),
                $"Limit={limit} walk lost or invented rule names. missing=" +
                $"{Repr(Sorted(names.Except(got)))} extra={Repr(Sorted(got.Except(names)))}");
        }

        // --- effect: RemoveTargets on one rule shrinks every subsequent page walk by exactly it. ----
        var removed = names[0];
        var remaining = names[1..];
        await ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = removed, EventBusName = bus, Ids = ["t1"], Force = true });
        var after = await Pages(ev, targetArn, bus, 3);
        var gotAfter = Sorted(after.SelectMany(p => p.RuleNames ?? throw new KeyNotFoundException("'RuleNames'")));
        Ensure(
            gotAfter.SequenceEqual(Sorted(remaining)),
            $"after RemoveTargets({removed}), a paged walk over '{targetArn}' must return exactly the " +
            $"remaining {remaining.Count} rules; got {Repr(gotAfter)}");
        Ensure(
            !gotAfter.Contains(removed),
            $"'{removed}' had its target removed and must not appear in any page of " +
            $"ListRuleNamesByTarget, got pages containing {Repr(gotAfter)}");
    }

    /// <summary>
    /// Walk ListRuleNamesByTarget to exhaustion, returning the raw pages.
    /// Bounded by max_pages so a NextToken that never ends fails instead of looping forever.
    /// </summary>
    private static async Task<List<ListRuleNamesByTargetResponse>> Pages(
        AmazonEventBridgeClient ev, string targetArn, string bus, int limit, int maxPages = 200)
    {
        var pages = new List<ListRuleNamesByTargetResponse>();
        string? token = null;
        while (true)
        {
            var call = new ListRuleNamesByTargetRequest { TargetArn = targetArn, EventBusName = bus, Limit = limit };
            if (token is not null)
            {
                call.NextToken = token;
            }

            var page = await ev.ListRuleNamesByTargetAsync(call);
            pages.Add(page);
            token = page.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                return pages;
            }

            Ensure(
                pages.Count < maxPages,
                $"ListRuleNamesByTarget still returned a NextToken after {pages.Count} pages over a " +
                $"collection of a dozen rules; the cursor never terminates");
        }
    }

    private static List<string> Sorted(IEnumerable<string> items) => [.. items.Order(StringComparer.Ordinal)];

    private static string Repr(IEnumerable<string> items) => $"[{string.Join(", ", items.Select(i => $"'{i}'"))}]";
}
