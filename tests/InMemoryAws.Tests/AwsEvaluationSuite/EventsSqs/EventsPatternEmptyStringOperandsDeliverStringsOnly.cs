// Translated from behavioral/events__sqs/check_events_pattern_empty_string_operands_deliver_strings_only.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json.Nodes;
using static InMemoryAws.Tests.AwsEvaluationSuite.EventsPatternMatrix;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.events-pattern-empty-string-operands-deliver-strings-only</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsPatternEmptyStringOperandsDeliverStringsOnly
{
    private static readonly JsonObject Rules = Parse("""
        {"empty-prefix": {"detail": {"k": [{"prefix": ""}]}}, "empty-suffix": {"detail": {"k": [{"suffix": ""}]}}, "empty-wildcard": {"detail": {"k": [{"wildcard": ""}]}}, "empty-ignore-case": {"detail": {"k": [{"equals-ignore-case": ""}]}}}
        """);

    private static readonly JsonObject Events = Parse("""
        {"text": {"k": "x"}, "empty": {"k": ""}, "number": {"k": 5}, "null": {"k": null}, "boolean": {"k": true}, "absent": {}}
        """);

    private static readonly Dictionary<string, HashSet<string>> Expected = new()
    {
        ["empty-prefix"] = ["empty", "text"],
        ["empty-suffix"] = ["empty", "text"],
        ["empty-wildcard"] = ["empty"],
        ["empty-ignore-case"] = ["empty"],
    };

    /// <summary>
    /// An empty prefix or suffix delivers every string value, the empty string included, but no number,
    /// boolean, null or absent field; an empty wildcard or ignore-case operand delivers only "".
    /// </summary>
    [Test]
    [DisplayName("events__sqs.events-pattern-empty-string-operands-deliver-strings-only")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var res = await Matrix(ctx, Rules, Events, Expected);
        AssertMatrix(res, Events, Expected);
        HashSet<string> nonStrings = ["number", "null", "boolean", "absent"];
        var reached = res.Ever
            .Where(kv => kv.Value.Overlaps(nonStrings))
            .ToDictionary(kv => kv.Key, kv => Sorted(kv.Value.Intersect(nonStrings)));
        Ensure(reached.Count == 0, $"a string operand delivered a non-string value: {Repr(reached)}");
    }
}
