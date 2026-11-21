// Translated from behavioral/events/check_put_rule_refuses_a_numeric_operand_no_double_represents_and_keeps_the_stored_pattern.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-rule-refuses-a-numeric-operand-no-double-represents-and-keeps-the-stored-pattern</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutRuleRefusesANumericOperandNoDoubleRepresentsAndKeepsTheStoredPattern
{
    // The operand sits between these two literals, so the column the refusal reports is derivable.
    private const string Head = """{"detail": {"n": [{"numeric": ["=", """;
    private const string Tail = "]}]}}";

    /// <summary>
    /// A numeric operand no binary64 double represents exactly is refused by PutRule, which leaves
    /// the rule's stored pattern and the pattern's verdicts untouched and mints no rule for a new name.
    /// </summary>
    [Test]
    [DisplayName("events.put-rule-refuses-a-numeric-operand-no-double-represents-and-keeps-the-stored-pattern")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        // Above 2**53 a double carries only the even integers, so these two differ by one and only one
        // of them survives the round trip.
        const long accepted = (1L << 53) + 2;
        const long refused = (1L << 53) + 1;
        Ensure(
            Representable(accepted) && !Representable(refused),
            $"derivation error: {accepted} must be an exact double and {refused} must not be, " +
            $"but round trips gave {(long)(double)accepted} and {(long)(double)refused}");

        var rule = FidelityContext.UniqueName("sim2real-numop-");
        await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = rule, EventPattern = Pattern(accepted), State = RuleState.ENABLED,
            Description = "sim2real numeric operand precision",
            Tags = [new Tag { Key = "sim2real", Value = "numeric-operand-precision" }],
        });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, Force = true }));
        var stored = (await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule })).EventPattern;
        Ensure(
            stored == Pattern(accepted),
            $"PutRule stored '{stored}' for the exactly-representable operand, expected '{Pattern(accepted)}'");

        // Breaking only the operand's representability must refuse the otherwise identical update.
        var (code, message) = await Refusal(() => ev.PutRuleAsync(new PutRuleRequest { Name = rule, EventPattern = Pattern(refused), State = RuleState.ENABLED }));
        Ensure(
            code == "InvalidEventPatternException",
            $"an operand no double represents gave '{code}', expected InvalidEventPatternException");
        var expected =
            $"Event pattern is not valid. Reason: Cannot compare number : {refused}\n" +
            $" at [Source: (String)\"{Pattern(refused)}\"; line: 1, column: {RefusalColumn(refused)}]";
        Ensure(message == expected, $"expected message '{expected}', got '{message}'");

        // The refused update must not have replaced what the rule already carried.
        var after = (await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule })).EventPattern;
        Ensure(
            after == Pattern(accepted),
            $"the refused update left the rule holding '{after}', expected '{Pattern(accepted)}'");
        Ensure(
            (await ev.TestEventPatternAsync(new TestEventPatternRequest { EventPattern = after, Event = Event(ctx, accepted) })).Result == true,
            $"the surviving pattern stopped matching the operand {accepted} it compares against");

        // Nor may the refusal leave a rule behind under a name that never held one.
        var fresh = FidelityContext.UniqueName("sim2real-numop-new-");
        var (code2, message2) = await Refusal(() => ev.PutRuleAsync(new PutRuleRequest { Name = fresh, EventPattern = Pattern(refused), State = RuleState.ENABLED }));
        Ensure(
            code2 == "InvalidEventPatternException",
            $"creating a rule with that operand gave '{code2}', expected InvalidEventPatternException");
        Ensure(message2 == expected, $"expected message '{expected}', got '{message2}'");
        var minted = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var page = await ev.ListRulesAsync(new ListRulesRequest { NamePrefix = fresh, NextToken = token });
            minted.UnionWith((page.Rules ?? []).Select(r => r.Name));
            token = page.NextToken;
        }
        while (!string.IsNullOrEmpty(token));

        Ensure(minted.Count == 0, $"the refused create still minted rule(s) {Py.Repr(minted.Order(StringComparer.Ordinal).ToList())}");

        // The legal neighbour of the refused input is accepted on the same name, so the refusal is
        // pinned to the operand's representability and not to the operand's magnitude.
        await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = fresh, EventPattern = Pattern(accepted), State = RuleState.ENABLED,
            Description = "sim2real numeric operand precision",
            Tags = [new Tag { Key = "sim2real", Value = "numeric-operand-precision" }],
        });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = fresh, Force = true }));
        var neighbour = (await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = fresh })).EventPattern;
        Ensure(
            neighbour == Pattern(accepted),
            $"the neighbouring operand stored '{neighbour}', expected '{Pattern(accepted)}'");
    }

    private static string Pattern(long operand) => $"{Head}{operand.ToString(CultureInfo.InvariantCulture)}{Tail}";

    /// <summary>The 1-based column of the brace closing the matcher object that holds <paramref name="operand"/>.</summary>
    private static int RefusalColumn(long operand) =>
        Head.Length + operand.ToString(CultureInfo.InvariantCulture).Length + "]}".Length;

    /// <summary>Whether <paramref name="operand"/> survives a round trip through an IEEE 754 binary64 double.</summary>
    private static bool Representable(long operand) => (long)(double)operand == operand;

    /// <summary>A complete EventBridge envelope whose `detail.n` carries <paramref name="value"/>.</summary>
    private static string Event(FidelityContext ctx, long value) =>
        "{\"version\": \"0\", \"id\": \"11111111-2222-3333-4444-555555555555\", " +
        "\"detail-type\": \"OrderPlaced\", \"source\": \"sim2real.orders\", " +
        "\"account\": \"123456789012\", \"time\": \"2024-01-01T00:00:00Z\", " +
        $"\"region\": \"{ctx.Region}\", \"resources\": [], \"detail\": {{\"n\": {value.ToString(CultureInfo.InvariantCulture)}}}}}";

    /// <summary>The (Code, Message) pair <paramref name="fn"/> is refused with; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Refusal<T>(Func<Task<T>> fn)
        where T : class
    {
        var (outcome, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {outcome}");
        }

        return (error.Code, error.Message);
    }
}
