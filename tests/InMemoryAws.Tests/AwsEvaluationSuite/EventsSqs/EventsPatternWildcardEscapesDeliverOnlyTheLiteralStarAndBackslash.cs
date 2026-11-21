// Translated from behavioral/events__sqs/check_events_pattern_wildcard_escapes_deliver_only_the_literal_star_and_backslash.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json.Nodes;
using static InMemoryAws.Tests.AwsEvaluationSuite.EventsPatternMatrix;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.events-pattern-wildcard-escapes-deliver-only-the-literal-star-and-backslash</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsPatternWildcardEscapesDeliverOnlyTheLiteralStarAndBackslash
{
    private static readonly JsonObject Rules = Parse("""
        {"escaped-star": {"detail": {"k": [{"wildcard": "a\\*b"}]}}, "escaped-backslash": {"detail": {"k": [{"wildcard": "a\\\\b"}]}}, "escaped-star-then-wildcard": {"detail": {"k": [{"wildcard": "a\\*b*"}]}}}
        """);

    private static readonly JsonObject Events = Parse("""
        {"literal-star": {"k": "a*b"}, "any-char": {"k": "axb"}, "backslash": {"k": "a\\b"}, "star-then-tail": {"k": "a*bzz"}, "no-middle": {"k": "ab"}}
        """);

    private static readonly Dictionary<string, HashSet<string>> Expected = new()
    {
        ["escaped-star"] = ["literal-star"],
        ["escaped-backslash"] = ["backslash"],
        ["escaped-star-then-wildcard"] = ["literal-star", "star-then-tail"],
    };

    /// <summary>
    /// A backslash-escaped star in a wildcard rule matches only a literal star and an escaped backslash
    /// only a literal backslash, while an unescaped trailing star still spans any run.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.events-pattern-wildcard-escapes-deliver-only-the-literal-star-and-backslash")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var res = await Matrix(ctx, Rules, Events, Expected);
        AssertMatrix(res, Events, Expected);
        Ensure(
            !res.Ever["escaped-star"].Contains("any-char"),
            $"an escaped star acted as a wildcard: {Repr(Sorted(res.Ever["escaped-star"]))}");
    }
}
