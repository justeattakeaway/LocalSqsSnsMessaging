// Translated from behavioral/events__sqs/check_events_put_rule_invalid_wildcard_escape_rejected_and_the_live_rule_keeps_matching.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json.Nodes;
using static InMemoryAws.Tests.AwsEvaluationSuite.EventsPatternMatrix;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.events-put-rule-invalid-wildcard-escape-rejected-and-the-live-rule-keeps-matching</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsPutRuleInvalidWildcardEscapeRejectedAndTheLiveRuleKeepsMatching
{
    private static readonly JsonObject Rules = Parse("""
        {"live": {"detail": {"k": [{"wildcard": "a*b"}]}}}
        """);

    private static readonly JsonObject Events = Parse("""
        {"spans": {"k": "axyzb"}, "other": {"k": "bxa"}}
        """);

    private static readonly Dictionary<string, HashSet<string>> Expected = new()
    {
        ["live"] = ["spans"],
    };

    /// <summary>
    /// PutRule refuses an unknown wildcard escape with its exact message, the stored pattern is unchanged,
    /// and the rule still delivers what its wildcard matches.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.events-put-rule-invalid-wildcard-escape-rejected-and-the-live-rule-keeps-matching")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var bad = Parse("""{"detail": {"k": [{"wildcard": "a\\b"}]}}""");
        const string reason = "Invalid escape character at pos 2";
        var res = await Matrix(ctx, Rules, Events, Expected, async (env, queues) => await RefuseUpdate(env, queues, bad));
        var (code, msg, badText, before, after) = (RefusedUpdate)res.Hook!;
        const string at = "\"wildcard\": \"a\\\\b\"";
        var column = badText.IndexOf(at, StringComparison.Ordinal) + at.Length + 1;
        var want = $"Event pattern is not valid. Reason: {reason}\n" +
            $" at [Source: (String)\"{badText}\"; line: 1, column: {column}]";
        Ensure(code == "InvalidEventPatternException", $"the refused PutRule raised {ReprStr(code)}: {ReprStr(msg)}");
        Ensure(msg == want, $"refusal message {ReprStr(msg)} != {ReprStr(want)}");
        Ensure(after == before, $"the refused update changed the stored pattern: {ReprStr(before)} -> {ReprStr(after)}");
        AssertMatrix(res, Events, Expected);
    }
}
