// Translated from behavioral/events/check_put_targets_new_id_on_full_rule_rejected_while_same_id_upsert_accepted.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-targets-new-id-on-full-rule-rejected-while-same-id-upsert-accepted</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutTargetsNewIdOnFullRuleRejectedWhileSameIdUpsertAccepted
{
    /// <summary>
    /// At five targets, PutTargets adding a sixth Id is refused with LimitExceededException leaving
    /// the same five, an existing-Id upsert is accepted, and a batch mixing both is refused whole.
    /// </summary>
    [Test]
    [DisplayName("events.put-targets-new-id-on-full-rule-rejected-while-same-id-upsert-accepted")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeTargets, "PutTargets adding a sixth target Id to a rule with five succeeds; AWS refuses it with LimitExceededException")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        // botocore retries LimitExceededException as a throttle; the refusals are asserted on one attempt.
        // (FidelityContext's clients never retry: MaxErrorRetry = 0.)
        var evOnce = ctx.Events;
        var rule = FidelityContext.UniqueName("sim2real-behav-rule-");
        await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = rule,
            EventPattern = """{"source": ["sim2real.cap"]}""",
            State = RuleState.DISABLED,
            Tags = [new Tag { Key = "sim2real", Value = "behavioral" }],
        });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule }));

        var arn = await QueueArn(ev, ctx, FidelityContext.UniqueName("cap-"));
        var ids = Enumerable.Range(0, 5).Select(i => $"t{i}").ToList();
        ctx.OnCleanup(() => ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, Ids = [.. ids, "t9"] }));
        var first = await ev.PutTargetsAsync(new PutTargetsRequest { Rule = rule, Targets = [.. ids.Select(tid => new Target { Id = tid, Arn = arn })] });
        Ensure(first.FailedEntryCount == 0, $"five targets on an empty rule were refused: FailedEntryCount={first.FailedEntryCount}");
        var initial = SortedIds(await Targets(ev, rule));
        if (!initial.SequenceEqual(ids))
        {
            throw Fail($"rule holds {Repr(SortedIds(await Targets(ev, rule)))}, expected {Repr(ids)}");
        }

        var (code, message) = await Error(() => evOnce.PutTargetsAsync(new PutTargetsRequest { Rule = rule, Targets = [new Target { Id = "t9", Arn = arn }] }));
        Ensure(code == "LimitExceededException", $"a sixth distinct Id on a full rule gave '{code}'");
        Ensure(
            message == "The requested resource exceeds the maximum number allowed.",
            $"a sixth distinct Id on a full rule was refused with message '{message}'");
        var afterRefusal = await Targets(ev, rule);
        Ensure(
            SortedIds(afterRefusal).SequenceEqual(ids),
            $"after the refused sixth target the rule holds {Repr(SortedIds(afterRefusal))}, expected {Repr(ids)}");

        var upsert = await ev.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            Targets = [new Target { Id = "t0", Arn = arn, Input = """{"v": 1}""" }],
        });
        Ensure(upsert.FailedEntryCount == 0, $"re-sending existing Id t0 at the cap was refused: FailedEntryCount={upsert.FailedEntryCount}");
        var afterUpsert = await Targets(ev, rule);
        Ensure(
            SortedIds(afterUpsert).SequenceEqual(ids),
            $"the upsert of t0 changed the Id set to {Repr(SortedIds(afterUpsert))}, expected {Repr(ids)}");
        Ensure(
            JsonNode.DeepEquals(JsonNode.Parse(afterUpsert["t0"].Input ?? "null"), JsonNode.Parse("""{"v": 1}""")),
            $"the upsert of t0 left Input {Repr(afterUpsert["t0"].Input)}, expected {{'v': 1}}");

        (code, message) = await Error(() => evOnce.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            Targets =
            [
                new Target { Id = "t1", Arn = arn, Input = """{"v": 2}""" },
                new Target { Id = "t9", Arn = arn },
            ],
        }));
        Ensure(code == "LimitExceededException", $"an upsert mixed with a sixth Id gave '{code}'");
        Ensure(
            message == "The requested resource exceeds the maximum number allowed.",
            $"an upsert mixed with a sixth Id was refused with message '{message}'");
        var afterMixed = await Targets(ev, rule);
        Ensure(
            SortedIds(afterMixed).SequenceEqual(ids),
            $"after the refused mixed batch the rule holds {Repr(SortedIds(afterMixed))}, expected {Repr(ids)}");
        Ensure(
            afterMixed["t1"].Input is null,
            $"the refused mixed batch still applied its upsert: t1 Input is {Repr(afterMixed["t1"].Input)}");
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
    private static async Task<Dictionary<string, Target>> Targets(AmazonEventBridgeClient ev, string rule) =>
        ((await ev.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule })).Targets
            ?? throw new KeyNotFoundException("'Targets'"))
        .ToDictionary(t => t.Id);

    private static List<string> SortedIds(Dictionary<string, Target> targets) => [.. targets.Keys.Order(StringComparer.Ordinal)];

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";

    private static string Repr(string? value) => value is null ? "None" : $"'{value}'";
}
