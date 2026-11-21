// Translated from behavioral/events/check_remove_targets_leaves_exactly_the_untouched_target_before_emptying_and_deleting_rule.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.remove-targets-leaves-exactly-the-untouched-target-before-emptying-and-deleting-rule</summary>
[Category("AwsEvaluationSuite")]
public sealed class RemoveTargetsLeavesExactlyTheUntouchedTargetBeforeEmptyingAndDeletingRule
{
    /// <summary>
    /// RemoveTargets on two of three Ids leaves exactly the untouched target; removing the
    /// last one empties the rule, letting DeleteRule succeed and DescribeRule 404 on it.
    /// </summary>
    [Test]
    [DisplayName("events.remove-targets-leaves-exactly-the-untouched-target-before-emptying-and-deleting-rule")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var rule = FidelityContext.UniqueName("sim2real-behav-rule-");
        var ruleResp = await ev.PutRuleAsync(new PutRuleRequest { Name = rule, EventBusName = bus, EventPattern = """{"source": ["sim2real.behav"]}""" });
        ctx.OnCleanup(() => CleanupRule(ev, rule, bus));

        var arnT1 = SqsTargetArn(ruleResp.RuleArn);
        var arnT2 = SqsTargetArn(ruleResp.RuleArn);
        var arnT3 = SqsTargetArn(ruleResp.RuleArn);
        var putResp = await ev.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            EventBusName = bus,
            Targets =
            [
                new Target { Id = "t1", Arn = arnT1 },
                new Target { Id = "t2", Arn = arnT2 },
                new Target { Id = "t3", Arn = arnT3 },
            ],
        });
        Ensure((putResp.FailedEntryCount ?? 0) == 0, $"PutTargets unexpectedly failed: FailedEntryCount={putResp.FailedEntryCount}");

        var before = await Targets(ev, rule, bus);
        var beforeIds = before.Select(t => t.Id).Order(StringComparer.Ordinal).ToList();
        Ensure(beforeIds.SequenceEqual(["t1", "t2", "t3"]), $"expected all three targets attached, got {Py.Repr(beforeIds)}");

        var partial = await ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, EventBusName = bus, Ids = ["t1", "t3"] });
        Ensure((partial.FailedEntryCount ?? 0) == 0, $"partial RemoveTargets unexpectedly failed: FailedEntryCount={partial.FailedEntryCount}");
        var afterPartial = await Targets(ev, rule, bus);
        Ensure(
            afterPartial.Count == 1 && IsBare(afterPartial[0], "t2", arnT2),
            $"expected exactly the untouched target t2 to remain after removing t1 and t3, got {Show(afterPartial)}");

        var full = await ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, EventBusName = bus, Ids = ["t2"] });
        Ensure((full.FailedEntryCount ?? 0) == 0, $"final RemoveTargets unexpectedly failed: FailedEntryCount={full.FailedEntryCount}");
        var afterFull = await Targets(ev, rule, bus);
        Ensure(afterFull.Count == 0, $"rule must have no targets after removing the last one, got {Show(afterFull)}");

        await ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, EventBusName = bus });
        var expected = ("ResourceNotFoundException", $"Rule {rule} does not exist on EventBus {bus}.");
        var err = await Error(() => ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule, EventBusName = bus }));
        Ensure(err == expected, $"expected {expected} from DescribeRule on a deleted rule, got {err}");
    }

    /// <summary>Syntactic SQS target ARN in ref_arn's partition/region/account (EventBridge does not verify existence).</summary>
    private static string SqsTargetArn(string refArn)
    {
        var p = refArn.Split(':');
        return $"arn:{p[1]}:sqs:{p[3]}:{p[4]}:sim2real-behav-q-{Guid.NewGuid().ToString("N")[..8]}";
    }

    private static async Task<List<Target>> Targets(IAmazonEventBridge ev, string rule, string bus) =>
        (await ev.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule, EventBusName = bus })).Targets ?? [];

    /// <summary><c>t == {"Id": id, "Arn": arn}</c>: the target carries those two members and nothing else.</summary>
    private static bool IsBare(Target t, string id, string arn) =>
        t.Id == id && t.Arn == arn && t.RoleArn is null && t.Input is null && t.InputPath is null
        && t.InputTransformer is null && t.DeadLetterConfig is null && t.RetryPolicy is null
        && t.SqsParameters is null && t.HttpParameters is null && t.KinesisParameters is null
        && t.EcsParameters is null && t.BatchParameters is null && t.RunCommandParameters is null
        && t.RedshiftDataParameters is null && t.SageMakerPipelineParameters is null
        && t.AppSyncParameters is null;

    private static string Show(List<Target> targets) =>
        "[" + string.Join(", ", targets.Select(t => $"{{'Id': '{t.Id}', 'Arn': '{t.Arn}'}}")) + "]";

    /// <summary>Remove any attached targets, then delete the rule (delete_rule refuses a rule with targets).</summary>
    private static async Task CleanupRule(IAmazonEventBridge client, string name, string bus)
    {
        try
        {
            var ids = (await Targets(client, name, bus)).Select(t => t.Id).ToList();
            if (ids.Count > 0)
            {
                await client.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, Ids = ids, EventBusName = bus });
            }
        }
        catch (Amazon.EventBridge.Model.ResourceNotFoundException)
        {
        }

        await client.DeleteRuleAsync(new DeleteRuleRequest { Name = name, EventBusName = bus, Force = true });
    }

    /// <summary>Call fn(); return its (code, message). An unexpected SUCCESS raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a ClientError, but the call SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }
}
