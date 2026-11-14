// Translated from behavioral/events/check_test_event_pattern_cidr_matcher_boundaries.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.test-event-pattern-cidr-matcher-boundaries</summary>
[Category("AwsEvaluationSuite")]
public sealed class TestEventPatternCidrMatcherBoundaries
{
    /// <summary>
    /// `cidr` is inclusive prefix containment for IPv4 and IPv6 (expected verdicts from ipaddress), a
    /// mask equal to the address width is refused, and wrong-family, non-IP and empty-array values miss.
    /// </summary>
    [Test]
    [DisplayName("events.test-event-pattern-cidr-matcher-boundaries")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        foreach (var cidr in new[] { "10.0.0.0/24", "192.168.5.0/30", "172.16.0.0/12", "2001:db8:abcd:12::/64" })
        {
            // ipaddress.ip_network(cidr): strict, so the host bits must be zero.
            var net = IPNetwork.Parse(cidr);
            var width = net.BaseAddress.GetAddressBytes().Length * 8;
            var first = net.BaseAddress;
            var last = Offset(first, (BigInteger.One << (width - net.PrefixLength)) - 1);
            var middle = Offset(first, (ToInteger(last) - ToInteger(first)) / 2);
            var below = Offset(first, -1);
            var above = Offset(last, 1);

            // Sanity on the derivation itself, so a bug in this file cannot masquerade as a target bug.
            Ensure(
                net.Contains(first) && net.Contains(last) && net.Contains(middle),
                $"derivation error: {first}/{middle}/{last} must all be inside {cidr}");
            Ensure(
                !net.Contains(below) && !net.Contains(above),
                $"derivation error: {below} and {above} must both be outside {cidr}");

            foreach (var addr in new[] { first, middle, last })
            {
                await Expect(
                    events,
                    $$$"""{"detail": {"sourceIPAddress": [{"cidr": "{{{cidr}}}"}]}}""",
                    $$"""{"sourceIPAddress": "{{addr}}"}""",
                    true,
                    $"{addr} is inside {cidr} (ipaddress says so), so cidr must match it");
            }

            foreach (var addr in new[] { below, above })
            {
                await Expect(
                    events,
                    $$$"""{"detail": {"sourceIPAddress": [{"cidr": "{{{cidr}}}"}]}}""",
                    $$"""{"sourceIPAddress": "{{addr}}"}""",
                    false,
                    $"{addr} is one address outside {cidr}, so cidr must NOT match it — a string-prefix " +
                    "implementation of cidr gets exactly this wrong");
            }
        }

        // AWS refuses a mask equal to the address width (/32, /128) although ipaddress accepts it;
        // the width - 1 mask is the positive control.
        await Expect(
            events,
            """{"detail": {"sourceIPAddress": [{"cidr": "203.0.113.0/31"}]}}""",
            """{"sourceIPAddress": "203.0.113.1"}""",
            true,
            "positive control: the narrowest VALID IPv4 mask (/31, one below the address width) " +
            "still evaluates a real match");
        var ipv4FullMaskErr = await ClientError(events, """{"detail": {"sourceIPAddress": [{"cidr": "203.0.113.0/32"}]}}""");
        Ensure(
            ipv4FullMaskErr.Code == "InvalidEventPatternException",
            "an IPv4 cidr mask equal to the address width (/32) must be refused as a malformed " +
            $"pattern, got {ipv4FullMaskErr}");
        Ensure(
            ipv4FullMaskErr.Message == "Event pattern is not valid. Reason: IPv4 mask bits must be < 32",
            $"unexpected message for the /32 refusal: '{ipv4FullMaskErr.Message}'");

        await Expect(
            events,
            """{"detail": {"sourceIPAddress": [{"cidr": "2001:db8::/127"}]}}""",
            """{"sourceIPAddress": "2001:db8::1"}""",
            true,
            "positive control: the narrowest VALID IPv6 mask (/127, one below the address width) " +
            "still evaluates a real match");
        var ipv6FullMaskErr = await ClientError(events, """{"detail": {"sourceIPAddress": [{"cidr": "2001:db8::/128"}]}}""");
        Ensure(
            ipv6FullMaskErr.Code == "InvalidEventPatternException",
            "an IPv6 cidr mask equal to the address width (/128) must be refused as a malformed " +
            $"pattern, got {ipv6FullMaskErr}");
        Ensure(
            ipv6FullMaskErr.Message == "Event pattern is not valid. Reason: IPv6 mask bits must be < 128",
            $"unexpected message for the /128 refusal: '{ipv6FullMaskErr.Message}'");

        // --- cross-family: an address of the wrong family is never inside the prefix. ---
        await Expect(
            events,
            """{"detail": {"sourceIPAddress": [{"cidr": "10.0.0.0/8"}]}}""",
            """{"sourceIPAddress": "2001:db8::1"}""",
            false,
            "an IPv6 address cannot be inside an IPv4 prefix");
        await Expect(
            events,
            """{"detail": {"sourceIPAddress": [{"cidr": "2001:db8::/32"}]}}""",
            """{"sourceIPAddress": "10.0.0.1"}""",
            false,
            "an IPv4 address cannot be inside an IPv6 prefix");

        // --- not an IP address at all. ---
        foreach (var junk in new[] { "not-an-ip", "10.0.0.256", "10.0.0", "" })
        {
            await Expect(
                events,
                """{"detail": {"sourceIPAddress": [{"cidr": "10.0.0.0/24"}]}}""",
                $$"""{"sourceIPAddress": "{{junk}}"}""",
                false,
                $"'{junk}' is not an IP address, so no prefix can contain it");
        }

        // --- the array-join rule extends to cidr: SOME element inside is a match, none is not. ---
        await Expect(
            events,
            """{"detail": {"sourceIPAddress": [{"cidr": "10.0.0.0/24"}]}}""",
            """{"sourceIPAddress": ["203.0.113.9", "10.0.0.7"]}""",
            true,
            "one element of the event's array is inside the prefix, which matches");
        await Expect(
            events,
            """{"detail": {"sourceIPAddress": [{"cidr": "10.0.0.0/24"}]}}""",
            """{"sourceIPAddress": ["203.0.113.9", "10.0.1.7"]}""",
            false,
            "no element of the event's array is inside the prefix, so nothing matches");
        await Expect(
            events,
            """{"detail": {"sourceIPAddress": [{"cidr": "10.0.0.0/24"}]}}""",
            """{"sourceIPAddress": []}""",
            false,
            "an empty event array cannot satisfy a cidr operand");

        // --- a cidr operand is one of several keys in a pattern: the other keys still AND. ---
        const string bothKeys = """{"detail": {"sourceIPAddress": [{"cidr": "10.0.0.0/24"}], "eventName": ["PutObject"]}}""";
        await Expect(
            events,
            bothKeys,
            """{"sourceIPAddress": "10.0.0.7", "eventName": "PutObject"}""",
            true,
            "both keys satisfied");
        await Expect(
            events,
            bothKeys,
            """{"sourceIPAddress": "10.0.0.7", "eventName": "GetObject"}""",
            false,
            "the cidr key matches but the sibling key does not — multiple keys are AND");
    }

    /// <summary><c>int(addr)</c>.</summary>
    private static BigInteger ToInteger(IPAddress address) =>
        new(address.GetAddressBytes(), isUnsigned: true, isBigEndian: true);

    /// <summary><c>ipaddress.ip_address(int(addr) + delta)</c>, in the same address family.</summary>
    private static IPAddress Offset(IPAddress address, BigInteger delta)
    {
        var length = address.GetAddressBytes().Length;
        var value = ToInteger(address) + delta;
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var padded = new byte[length];
        bytes.CopyTo(padded, length - bytes.Length);
        return new IPAddress(padded);
    }

    /// <summary>
    /// A complete EventBridge envelope carrying <paramref name="detail"/> (JSON text); account and
    /// region are placeholders because TestEventPattern never touches either.
    /// </summary>
    private static JsonObject Envelope(string detail) => new()
    {
        ["id"] = "00000000-0000-0000-0000-000000000000",
        ["detail-type"] = "CidrProbe",
        ["source"] = "sim2real.cidr",
        ["account"] = "123456789012",
        ["time"] = "2020-01-01T00:00:00Z",
        ["region"] = "us-east-1",
        ["resources"] = new JsonArray(),
        ["detail"] = JsonNode.Parse(detail),
    };

    /// <summary>TestEventPattern's boolean verdict for one (pattern, event) pair.</summary>
    private static async Task<bool> Matches(AmazonEventBridgeClient events, string pattern, string detail)
    {
        var resp = await events.TestEventPatternAsync(new TestEventPatternRequest
        {
            EventPattern = pattern, Event = PyDumps(Envelope(detail)),
        });
        var result = resp.Result;
        Ensure(
            result is not null,
            "TestEventPattern.Result must be a JSON boolean, got None " +
            $"(NoneType) for pattern {pattern}");
        return result.Value;
    }

    /// <summary>The botocore Error dict TestEventPattern raises for <paramref name="pattern"/>; an unexpected success raises.</summary>
    private static async Task<AwsError> ClientError(AmazonEventBridgeClient events, string pattern)
    {
        var (result, error) = await FidelityContext.Capture(() => events.TestEventPatternAsync(new TestEventPatternRequest
        {
            EventPattern = pattern, Event = PyDumps(Envelope("""{"sourceIPAddress": "1.2.3.4"}""")),
        }));
        return error ?? throw Fail($"TestEventPattern accepted {pattern} and returned {{'Result': {result!.Result}}}; AWS refuses it");
    }

    /// <summary>Assert TestEventPattern's verdict, naming the pattern and event that disagreed.</summary>
    private static async Task Expect(AmazonEventBridgeClient events, string pattern, string detail, bool want, string why)
    {
        var got = await Matches(events, pattern, detail);
        Ensure(
            got == want,
            $"cidr verdict wrong: expected {want}, got {got}. {why}\n" +
            $"  pattern = {pattern}\n" +
            $"  detail  = {detail}");
    }

    /// <summary><c>json.dumps(value)</c> with Python's default separators (", " and ": ").</summary>
    private static string PyDumps(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(", ", o.Select(kv => $"{JsonSerializer.Serialize(kv.Key)}: {PyDumps(kv.Value)}")) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(PyDumps)) + "]",
        _ => node.ToJsonString(),
    };
}
