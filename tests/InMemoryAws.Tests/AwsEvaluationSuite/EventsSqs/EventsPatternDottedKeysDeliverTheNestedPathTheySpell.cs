// Translated from behavioral/events__sqs/check_events_pattern_dotted_keys_deliver_the_nested_path_they_spell.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json.Nodes;
using static InMemoryAws.Tests.AwsEvaluationSuite.EventsPatternMatrix;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.events-pattern-dotted-keys-deliver-the-nested-path-they-spell</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsPatternDottedKeysDeliverTheNestedPathTheySpell
{
    private static readonly JsonObject Rules = Parse("""
        {"dotted-in-detail": {"detail": {"a.b": ["x"]}}, "dotted-from-top": {"detail.a.b": ["x"]}, "nested": {"detail": {"a": {"b": ["x"]}}}}
        """);

    private static readonly JsonObject Events = Parse("""
        {"nested": {"a": {"b": "x"}}, "literal-dotted": {"a.b": "x"}, "array-leaf": {"a": {"b": ["z", "x"]}}, "other-value": {"a": {"b": "y"}}, "shallow": {"a": "x"}}
        """);

    private static readonly Dictionary<string, HashSet<string>> Expected = new()
    {
        ["dotted-in-detail"] = ["array-leaf", "literal-dotted", "nested"],
        ["dotted-from-top"] = ["array-leaf", "literal-dotted", "nested"],
        ["nested"] = ["array-leaf", "literal-dotted", "nested"],
    };

    /// <summary>
    /// A dotted pattern key, inside detail or from the top level, addresses the nested path it spells,
    /// so it delivers exactly what the nested pattern delivers, a literal dotted event key included.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.events-pattern-dotted-keys-deliver-the-nested-path-they-spell")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var res = await Matrix(ctx, Rules, Events, Expected);
        AssertMatrix(res, Events, Expected);
        Ensure(
            res.Confirmed["dotted-in-detail"].SetEquals(res.Confirmed["nested"]) && res.Confirmed["nested"].SetEquals(res.Confirmed["dotted-from-top"]),
            $"dotted and nested forms of one path delivered different sets: {Repr(res.Confirmed)}");
    }
}
