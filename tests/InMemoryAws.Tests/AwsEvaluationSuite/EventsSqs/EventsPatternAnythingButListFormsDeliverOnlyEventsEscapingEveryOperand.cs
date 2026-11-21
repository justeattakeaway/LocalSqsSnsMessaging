// Translated from behavioral/events__sqs/check_events_pattern_anything_but_list_forms_deliver_only_events_escaping_every_operand.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json.Nodes;
using static InMemoryAws.Tests.AwsEvaluationSuite.EventsPatternMatrix;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.events-pattern-anything-but-list-forms-deliver-only-events-escaping-every-operand</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsPatternAnythingButListFormsDeliverOnlyEventsEscapingEveryOperand
{
    private static readonly JsonObject Rules = Parse("""
        {"prefix-list": {"detail": {"k": [{"anything-but": {"prefix": ["ab", "cd"]}}]}}, "suffix-list": {"detail": {"k": [{"anything-but": {"suffix": ["ab", "cd"]}}]}}, "wildcard-list": {"detail": {"k": [{"anything-but": {"wildcard": ["a*", "*z"]}}]}}}
        """);

    private static readonly JsonObject Events = Parse("""
        {"ab-lead": {"k": "abx"}, "cd-lead": {"k": "cdx"}, "cd-tail": {"k": "xcd"}, "z-tail": {"k": "qz"}, "plain": {"k": "qq"}, "mixed-case": {"k": "cD"}, "number": {"k": 5}, "absent": {}}
        """);

    private static readonly Dictionary<string, HashSet<string>> Expected = new()
    {
        ["prefix-list"] = ["cd-tail", "mixed-case", "number", "plain", "z-tail"],
        ["suffix-list"] = ["ab-lead", "cd-lead", "mixed-case", "number", "plain", "z-tail"],
        ["wildcard-list"] = ["cd-lead", "cd-tail", "mixed-case", "number", "plain"],
    };

    /// <summary>
    /// Each anything-but prefix, suffix and wildcard list excludes an event matching ANY operand,
    /// admits a number and never an absent field, by live delivery and TestEventPattern alike.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.events-pattern-anything-but-list-forms-deliver-only-events-escaping-every-operand")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var res = await Matrix(ctx, Rules, Events, Expected);
        AssertMatrix(res, Events, Expected);
        Ensure(
            !res.Ever.Values.Any(v => v.Contains("absent")),
            $"an event without the field reached an anything-but rule: {Repr(res.Ever)}");
    }
}
