// Translated from behavioral/events/check_put_targets_duplicate_id_in_one_request_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-targets-duplicate-id-in-one-request-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutTargetsDuplicateIdInOneRequestRejected
{
    /// <summary>
    /// One PutTargets request naming the same target Id twice is refused whole with
    /// ValidationException naming the Id, leaving the existing target untouched; the same entries under
    /// distinct Ids are then accepted.
    /// </summary>
    [Test]
    [DisplayName("events.put-targets-duplicate-id-in-one-request-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var rule = FidelityContext.UniqueName("sim2real-behav-rule-");
        await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = rule,
            EventPattern = """{"source": ["sim2real.dup"]}""",
            State = RuleState.DISABLED,
            Tags = [new Tag { Key = "sim2real", Value = "behavioral" }],
        });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule }));

        var arnA = await QueueArn(ev, ctx, FidelityContext.UniqueName("dup-a-"));
        var arnB = await QueueArn(ev, ctx, FidelityContext.UniqueName("dup-b-"));
        ctx.OnCleanup(() => ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, Ids = ["keep", "dup", "dup-2"] }));
        await ev.PutTargetsAsync(new PutTargetsRequest { Rule = rule, Targets = [new Target { Id = "keep", Arn = arnA }] });

        var (code, message) = await Error(() => ev.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            Targets =
            [
                new Target { Id = "dup", Arn = arnA },
                new Target { Id = "dup", Arn = arnB },
            ],
        }));
        Ensure(code == "ValidationException", $"two entries sharing Id 'dup' gave '{code}'");
        Ensure(
            message == "Parameter targets is not valid. Reason: More than one target with Id 'dup' is provided.",
            $"two entries sharing Id 'dup' were refused with message '{message}'");
        var after = await TargetArns(ev, rule);
        Ensure(
            DictEquals(after, new() { ["keep"] = arnA }),
            $"after the refused duplicate-Id request the rule holds {Sorted(after.Keys)}, expected only 'keep'");

        var ok = await ev.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            Targets = [new Target { Id = "dup", Arn = arnA }, new Target { Id = "dup-2", Arn = arnB }],
        });
        Ensure(ok.FailedEntryCount == 0, $"the same entries under distinct Ids were refused: FailedEntryCount={ok.FailedEntryCount}");
        var final = await TargetArns(ev, rule);
        Ensure(
            DictEquals(final, new() { ["keep"] = arnA, ["dup"] = arnA, ["dup-2"] = arnB }),
            $"after the distinct-Id request the rule holds Ids {Sorted(final.Keys)}, expected keep, dup, dup-2");
    }

    /// <summary>The (Error.Code, Error.Message) <paramref name="fn"/> raises. An unexpected success raises too.</summary>
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

    // Python indexes list_targets_by_rule(...)["Targets"], which raises KeyError when the response omits it.
    private static async Task<Dictionary<string, string>> TargetArns(AmazonEventBridgeClient ev, string rule) =>
        ((await ev.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule })).Targets
            ?? throw new KeyNotFoundException("'Targets'"))
        .ToDictionary(t => t.Id, t => t.Arn);

    private static bool DictEquals(Dictionary<string, string> a, Dictionary<string, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static string Sorted(IEnumerable<string> items) => "[" + string.Join(", ", items.Order(StringComparer.Ordinal).Select(i => $"'{i}'")) + "]";
}
