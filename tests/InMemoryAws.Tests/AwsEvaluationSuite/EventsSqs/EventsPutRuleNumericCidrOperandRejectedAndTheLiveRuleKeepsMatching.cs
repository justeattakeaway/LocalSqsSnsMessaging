// Translated from behavioral/events__sqs/check_events_put_rule_numeric_cidr_operand_rejected_and_the_live_rule_keeps_matching.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json.Nodes;
using static InMemoryAws.Tests.AwsEvaluationSuite.EventsPatternMatrix;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.events-put-rule-numeric-cidr-operand-rejected-and-the-live-rule-keeps-matching</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsPutRuleNumericCidrOperandRejectedAndTheLiveRuleKeepsMatching
{
    private static readonly JsonObject Rules = Parse("""
        {"live": {"detail": {"ip": [{"cidr": "10.0.0.0/24"}]}}}
        """);

    private static readonly JsonObject Events = Parse("""
        {"inside": {"ip": "10.0.0.9"}, "outside": {"ip": "10.0.1.9"}}
        """);

    private static readonly Dictionary<string, HashSet<string>> Expected = new()
    {
        ["live"] = ["inside"],
    };

    /// <summary>
    /// PutRule refuses a numeric cidr operand with its exact message, the stored pattern is unchanged,
    /// and the rule still delivers only addresses inside its block.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.events-put-rule-numeric-cidr-operand-rejected-and-the-live-rule-keeps-matching")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var bad = Parse("""{"detail": {"ip": [{"cidr": 5}]}}""");
        const string reason = "prefix match pattern must be a string";
        var res = await Matrix(ctx, Rules, Events, Expected, async (env, queues) => await RefuseUpdate(env, queues, bad));
        var (code, msg, badText, before, after) = (RefusedUpdate)res.Hook!;
        const string at = "\"cidr\": 5";
        var column = badText.IndexOf(at, StringComparison.Ordinal) + at.Length + 1;
        var want = $"Event pattern is not valid. Reason: {reason}\n" +
            $" at [Source: (String)\"{badText}\"; line: 1, column: {column}]";
        Ensure(code == "InvalidEventPatternException", $"the refused PutRule raised {ReprStr(code)}: {ReprStr(msg)}");
        Ensure(msg == want, $"refusal message {ReprStr(msg)} != {ReprStr(want)}");
        Ensure(after == before, $"the refused update changed the stored pattern: {ReprStr(before)} -> {ReprStr(after)}");
        AssertMatrix(res, Events, Expected);
    }
}
