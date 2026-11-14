// Translated from behavioral/events/check_delete_rule_without_event_bus_name_leaves_custom_bus_rule_in_place.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.delete-rule-without-event-bus-name-leaves-custom-bus-rule-in-place</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteRuleWithoutEventBusNameLeavesCustomBusRuleInPlace
{
    /// <summary>
    /// DeleteRule without EventBusName succeeds but leaves a same-named custom-bus rule intact;
    /// DeleteRule with the bus ARN removes it, and DescribeRule then names the custom bus.
    /// </summary>
    [Test]
    [DisplayName("events.delete-rule-without-event-bus-name-leaves-custom-bus-rule-in-place")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeErrorMessages, "DescribeRule of a deleted custom-bus rule says '... does not exist on event bus <bus>.' instead of '... on EventBus <bus>.'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        var busArn = (await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real", Value = "behavioral" }] })).EventBusArn;
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var rule = FidelityContext.UniqueName("sim2real-behav-rule-");
        var pattern = new JsonObject { ["source"] = new JsonArray("sim2real.survivor") };
        await ev.PutRuleAsync(new PutRuleRequest { Name = rule, EventBusName = bus, EventPattern = """{"source": ["sim2real.survivor"]}""" });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, EventBusName = bus }));

        // Targets the default bus, where the rule is absent; DeleteRule of an absent rule succeeds.
        await ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule });

        var survivor = await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule, EventBusName = bus });
        Ensure(survivor.Name == rule, $"DescribeRule on the custom bus returned Name '{survivor.Name}'");
        Ensure(
            JsonNode.DeepEquals(JsonNode.Parse(survivor.EventPattern), pattern),
            $"the custom-bus rule's pattern after a default-bus DeleteRule is '{survivor.EventPattern}'");
        var listed = ((await ev.ListRulesAsync(new ListRulesRequest { EventBusName = bus, NamePrefix = rule })).Rules ?? []).Select(r => r.Name).ToList();
        Ensure(
            listed.SequenceEqual([rule]),
            $"ListRules on the custom bus after a default-bus DeleteRule shows [{string.Join(", ", listed.Select(n => $"'{n}'"))}]");

        await ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, EventBusName = busArn });
        var (code, message) = await Error(() => ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule, EventBusName = bus }));
        Ensure(code == "ResourceNotFoundException", $"DescribeRule after the bus-scoped DeleteRule gave '{code}'");
        Ensure(
            message == $"Rule {rule} does not exist on EventBus {bus}.",
            $"DescribeRule after the bus-scoped DeleteRule said '{message}'");
    }

    /// <summary>The (Error.Code, Error.Message) <c>fn</c> raises. An unexpected success raises too.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }
}
