// Translated from behavioral/events/check_put_rule_update_replaces_omitted_fields_and_ignores_tags.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-rule-update-replaces-omitted-fields-and-ignores-tags</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutRuleUpdateReplacesOmittedFieldsAndIgnoresTags
{
    /// <summary>
    /// PutRule on an existing rule replaces it: the new pattern applies, omitted Description and
    /// State reset (State back to ENABLED), the ARN, targets and original tag survive, and update Tags
    /// are ignored.
    /// </summary>
    [Test]
    [DisplayName("events.put-rule-update-replaces-omitted-fields-and-ignores-tags")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeTags, "ListTagsForResource is not supported (NotSupportedException); AWS lists the rule's original tag")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var rule = FidelityContext.UniqueName("sim2real-behav-rule-");
        var originalTags = new Dictionary<string, string> { ["sim2real"] = "behavioral" };
        var createdArn = (await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = rule,
            EventPattern = """{"source": ["sim2real.before"]}""",
            Description = "sim2real original description",
            State = RuleState.DISABLED,
            Tags = [.. originalTags.Select(kv => new Tag { Key = kv.Key, Value = kv.Value })],
        })).RuleArn;
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule }));

        var partition = createdArn.Split(':')[1];
        var targetArn = $"arn:{partition}:sqs:{ctx.Region}:{ctx.Account}:" + FidelityContext.UniqueName("sim2real-behav-q-");
        await ev.PutTargetsAsync(new PutTargetsRequest { Rule = rule, Targets = [new Target { Id = "kept", Arn = targetArn }] });
        ctx.OnCleanup(() => ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, Ids = ["kept"] }));

        var before = await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule });
        Ensure(before.State?.Value == "DISABLED", $"the rule was created DISABLED but reads '{before.State?.Value}'");
        Ensure(
            before.Description == "sim2real original description",
            $"the rule's Description reads {Repr(before.Description)} after create");

        var updatedArn = (await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = rule,
            EventPattern = """{"source": ["sim2real.after"]}""",
            Tags = [new Tag { Key = "sim2real-added-on-update", Value = "ignored" }],
        })).RuleArn;
        Ensure(updatedArn == createdArn, "updating the rule changed its ARN");

        var after = await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule });
        var afterPattern = after.EventPattern ?? throw new KeyNotFoundException("'EventPattern'");
        Ensure(
            JsonNode.DeepEquals(JsonNode.Parse(afterPattern), JsonNode.Parse("""{"source": ["sim2real.after"]}""")),
            $"the updated rule's EventPattern is '{afterPattern}', expected source sim2real.after");
        Ensure(
            after.Description is null,
            $"an update that omitted Description kept {Repr(after.Description)}; PutRule replaces the rule");
        Ensure(
            after.State?.Value == "ENABLED",
            $"an update that omitted State left the rule '{after.State?.Value}', expected the default ENABLED");
        if (!DictEquals(await Tags(ev, createdArn), originalTags))
        {
            throw Fail(
                $"after an update carrying a new tag the rule's tags are {Show(await Tags(ev, createdArn))}, expected only {Show(originalTags)}");
        }

        var kept = ((await ev.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule })).Targets
            ?? throw new KeyNotFoundException("'Targets'")).Select(t => t.Id).ToList();
        Ensure(
            kept.Count == 1 && kept[0] == "kept",
            $"the rule's targets after the update are [{string.Join(", ", kept.Select(k => $"'{k}'"))}], expected ['kept']");
    }

    // Python indexes ["Tags"], which raises KeyError when the response omits it.
    private static async Task<Dictionary<string, string>> Tags(AmazonEventBridgeClient ev, string arn) =>
        ((await ev.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceARN = arn })).Tags
            ?? throw new KeyNotFoundException("'Tags'"))
        .ToDictionary(t => t.Key, t => t.Value);

    private static bool DictEquals(Dictionary<string, string> a, Dictionary<string, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static string Show(Dictionary<string, string> d) => "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";

    private static string Repr(string? value) => value is null ? "None" : $"'{value}'";
}
