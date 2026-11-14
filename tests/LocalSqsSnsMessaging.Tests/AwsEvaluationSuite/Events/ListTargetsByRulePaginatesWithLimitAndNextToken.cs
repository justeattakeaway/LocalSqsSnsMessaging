// Translated from behavioral/events/check_list_targets_by_rule_paginates_with_limit_and_next_token.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.list-targets-by-rule-paginates-with-limit-and-next-token</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListTargetsByRulePaginatesWithLimitAndNextToken
{
    /// <summary>
    /// ListTargetsByRule pages five targets with Limit=2 as 2/2/1, each keeping its Arn and Input,
    /// the last page has no NextToken, an unpaged call returns all five, and a forged NextToken is
    /// refused.
    /// </summary>
    [Test]
    [DisplayName("events.list-targets-by-rule-paginates-with-limit-and-next-token")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var rule = FidelityContext.UniqueName("sim2real-behav-rule-");
        await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = rule,
            EventPattern = """{"source": ["sim2real.pagination"]}""",
            State = RuleState.DISABLED,
            Tags = [new Tag { Key = "sim2real", Value = "behavioral" }],
        });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule }));

        var ids = Enumerable.Range(0, 5).Select(i => $"target-{i}").ToList();
        var targets = new List<Target>();
        for (var i = 0; i < ids.Count; i++)
        {
            targets.Add(new Target { Id = ids[i], Arn = await QueueArn(ev, ctx, $"page-{i}"), Input = $$"""{"n": {{i}}}""" });
        }

        var resp = await ev.PutTargetsAsync(new PutTargetsRequest { Rule = rule, Targets = targets });
        ctx.OnCleanup(() => ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, Ids = ids }));
        Ensure(resp.FailedEntryCount == 0, $"PutTargets of five targets reported failures: {resp.FailedEntryCount} {string.Join(", ", (resp.FailedEntries ?? []).Select(f => $"({f.TargetId}, {f.ErrorCode}, {f.ErrorMessage})"))}");

        var whole = await ev.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule });
        var wholeIds = Sorted(TargetsOf(whole).Select(t => t.Id));
        Ensure(
            wholeIds.SequenceEqual(ids),
            $"an unpaged ListTargetsByRule returned Ids {Repr(wholeIds)}, " +
            $"expected {Repr(ids)}");
        Ensure(
            whole.NextToken is null,
            $"an unpaged ListTargetsByRule holding all five targets carried NextToken " +
            $"'{whole.NextToken}'");

        var pageSizes = new List<int>();
        var seen = new List<Target>();
        string? token = null;
        for (var n = 0; n < 10; n++)
        {
            var request = new ListTargetsByRuleRequest { Rule = rule, Limit = 2 };
            if (!string.IsNullOrEmpty(token))
            {
                request.NextToken = token;
            }

            var page = await ev.ListTargetsByRuleAsync(request);
            pageSizes.Add(TargetsOf(page).Count);
            seen.AddRange(TargetsOf(page));
            token = page.NextToken;
            if (string.IsNullOrEmpty(token))
            {
                break;
            }
        }

        Ensure(token is null, $"ListTargetsByRule with Limit=2 was still paging after 10 pages: {Repr(pageSizes)}");
        Ensure(pageSizes.SequenceEqual([2, 2, 1]), $"Limit=2 over five targets gave page sizes {Repr(pageSizes)}, expected [2, 2, 1]");
        var seenIds = seen.Select(t => t.Id).ToList();
        Ensure(
            Sorted(seenIds).SequenceEqual(ids),
            $"the pages held Ids {Repr(seenIds)}: expected each of {Repr(ids)} exactly once");
        var byId = new Dictionary<string, Target>();
        foreach (var t in seen)
        {
            byId[t.Id] = t;
        }

        for (var i = 0; i < ids.Count; i++)
        {
            var tid = ids[i];
            Ensure(
                byId[tid].Arn == targets[i].Arn,
                $"target '{tid}' came back on its page with Arn '{byId[tid].Arn}', " +
                $"expected '{targets[i].Arn}'");
            Ensure(
                JsonNode.DeepEquals(JsonNode.Parse(byId[tid].Input ?? throw new KeyNotFoundException("'Input'")), new JsonObject { ["n"] = i }),
                $"target '{tid}' came back on its page with Input '{byId[tid].Input}', expected n={i}");
        }

        var (code, message) = await Error(
            () => ev.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule, Limit = 2, NextToken = "sim2real-not-a-token" }));
        Ensure(code == "ValidationException", $"a NextToken the service never issued gave '{code}'");
        Ensure(
            message == "Parameter NextToken is not valid.",
            $"a NextToken the service never issued was refused with message '{message}'");
    }

    /// <summary>The (Error.Code, Error.Message) <c>fn</c> raises. An unexpected success raises too.</summary>
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

    /// <summary>
    /// A syntactically valid SQS queue ARN in this account and region (EventBridge does not
    /// require a target to exist at PutTargets time).
    /// </summary>
    private static async Task<string> QueueArn(AmazonEventBridgeClient ev, FidelityContext ctx, string suffix)
    {
        var partition = (await ev.DescribeEventBusAsync(new DescribeEventBusRequest { Name = "default" })).Arn.Split(':')[1];
        return $"arn:{partition}:sqs:{ctx.Region}:{ctx.Account}:sim2real-behav-{suffix}";
    }

    /// <summary><c>page["Targets"]</c>: a missing key raises KeyError, as in Python.</summary>
    private static List<Target> TargetsOf(ListTargetsByRuleResponse resp) => resp.Targets ?? throw new KeyNotFoundException("'Targets'");

    private static List<string> Sorted(IEnumerable<string> items) => [.. items.Order(StringComparer.Ordinal)];

    private static string Repr(IEnumerable<string> items) => $"[{string.Join(", ", items.Select(i => $"'{i}'"))}]";

    private static string Repr(IEnumerable<int> items) => $"[{string.Join(", ", items)}]";
}
