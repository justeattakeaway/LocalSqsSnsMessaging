// Translated from behavioral/events/check_test_event_pattern_numeric_skips_an_event_number_no_double_represents.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.test-event-pattern-numeric-skips-an-event-number-no-double-represents</summary>
[Category("AwsEvaluationSuite")]
public sealed class TestEventPatternNumericSkipsAnEventNumberNoDoubleRepresents
{
    // json.dumps of the Python patterns: 1e30 is written 1e+30.
    private const string Above = """{"detail": {"n": [{"numeric": [">", 1]}]}}""";
    private const string Below = """{"detail": {"n": [{"numeric": ["<", 1e+30]}]}}""";
    private const string Band = """{"detail": {"n": [{"numeric": [">", 1, "<", 1e+30]}]}}""";
    private const string Present = """{"detail": {"n": [{"exists": true}]}}""";

    /// <summary>
    /// A numeric comparison reads an event number only where a binary64 double represents it
    /// exactly; exact-value matching and `exists` still see the very numbers it skips.
    /// </summary>
    [Test]
    [DisplayName("events.test-event-pattern-numeric-skips-an-event-number-no-double-represents")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;

        // Above 2**53 a double carries only the even integers, so a run of consecutive values alternates
        // between representable and not, which no single verdict could be mistaken for.
        var values = Enumerable.Range(1, 8).Select(offset => (1L << 53) + offset).ToList();
        var exact = values.Where(Representable).ToList();
        Ensure(
            exact.Count == 4 && exact.SequenceEqual(values.Where(v => v % 2 == 0)),
            $"derivation error: exactly the even half of {InMemoryAws.Tests.AwsEvaluationSuite.Py.Repr(values)} must be exact doubles, got {InMemoryAws.Tests.AwsEvaluationSuite.Py.Repr(exact)}");

        foreach (var value in values)
        {
            var want = Representable(value);
            var @event = Event(ctx, value);

            // Both directions are satisfied by the magnitude of every value here, so a false verdict
            // means the comparison never read a number, not that the comparison came out the other way.
            await Expect(ev, Above, @event, want, $"a lower-bounded comparison reads {value} only as an exact double");
            await Expect(ev, Below, @event, want, $"an upper-bounded comparison reads {value} only as an exact double");
            await Expect(ev, Band, @event, want, $"a two-sided range reads {value} only as an exact double");

            // The same literal is still the value the event carries, so the skip belongs to the
            // comparison alone.
            await Expect(ev, $"{{\"detail\": {{\"n\": [{Text(value)}]}}}}", @event, true,
                $"exact-value matching must match the event's own number {value}");
            await Expect(ev, Present, @event, true, $"the field carrying {value} must still be present to `exists`");
        }
    }

    /// <summary>Whether <paramref name="value"/> survives a round trip through an IEEE 754 binary64 double.</summary>
    private static bool Representable(long value) => (long)(double)value == value;

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A complete EventBridge envelope whose `detail.n` carries <paramref name="value"/>.</summary>
    private static string Event(FidelityContext ctx, long value) =>
        "{\"version\": \"0\", \"id\": \"11111111-2222-3333-4444-555555555555\", " +
        "\"detail-type\": \"OrderPlaced\", \"source\": \"sim2real.orders\", " +
        "\"account\": \"123456789012\", \"time\": \"2024-01-01T00:00:00Z\", " +
        $"\"region\": \"{ctx.Region}\", \"resources\": [], \"detail\": {{\"n\": {Text(value)}}}}}";

    /// <summary>Assert TestEventPattern's verdict, quoting the pattern and event that disagreed.</summary>
    private static async Task Expect(IAmazonEventBridge ev, string patternText, string @event, bool want, string why)
    {
        var got = (await ev.TestEventPatternAsync(new TestEventPatternRequest { EventPattern = patternText, Event = @event })).Result;
        Ensure(
            got == want,
            $"{why}: expected Result={Py(want)}, got {(got is { } g ? Py(g) : "None")}\n" +
            $"  pattern = {patternText}\n  event = {@event}");
    }

    private static string Py(bool value) => value ? "True" : "False";
}
