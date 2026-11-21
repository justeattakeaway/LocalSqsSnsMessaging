// Translated from behavioral/events/check_test_event_pattern_cidr_operand_masks_its_host_bits.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.test-event-pattern-cidr-operand-masks-its-host-bits</summary>
[Category("AwsEvaluationSuite")]
public sealed class TestEventPatternCidrOperandMasksItsHostBits
{
    // Each operand names an address with host bits set, so the block it stands for has to be derived.
    private static readonly (string Host, int Bits)[] Operands =
        [("198.51.100.130", 25), ("203.0.113.200", 26), ("192.0.2.130", 25), ("2001:db8::5", 64)];

    /// <summary>
    /// A `cidr` operand carrying host bits stands for the block that contains it, so it admits
    /// exactly that block's addresses and agrees with the same block written canonically.
    /// </summary>
    [Test]
    [DisplayName("events.test-event-pattern-cidr-operand-masks-its-host-bits")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        foreach (var (host, bits) in Operands)
        {
            var operand = $"{host}/{bits}";
            var block = Network.Of(host, bits);
            Ensure(
                Network.Value(IPAddress.Parse(host)) != block.First,
                $"derivation error: {operand} must carry host bits, but {host} already is the " +
                $"network address of {block}");
            var canonical = $"{block.Format(block.First)}/{bits}";
            var below = block.First - 1;
            (BigInteger Address, bool Want, string Why)[] probes =
            [
                (block.First, true, "the block's first address"),
                (block.First + 1, true, "an address just inside the block"),
                (block.Last, true, "the block's last address"),
                (block.Last + 1, false, "the first address above the block"),
                (below, false, "the last address below the block"),
            ];
            foreach (var (address, want, why) in probes)
            {
                // The masked operand and the canonical block must be indistinguishable to the matcher.
                var text = block.Format(address);
                Ensure(
                    block.Contains(address) == want,
                    $"derivation error: {text} in {block} is {Py(block.Contains(address))}, expected {Py(want)}");
                var @event = Event(ctx, text);
                await Expect(ev, operand, @event, want, $"{why} ({text}) against the host-bit operand {operand}");
                await Expect(ev, canonical, @event, want, $"{why} ({text}) against the canonical block {canonical}");
            }
        }
    }

    /// <summary>A complete EventBridge envelope whose `detail.ip` carries <paramref name="address"/>.</summary>
    private static string Event(FidelityContext ctx, string address) =>
        "{\"version\": \"0\", \"id\": \"5c8d0f31-7b42-4a19-8e60-d3f17a90cb24\", " +
        "\"detail-type\": \"ConnectionObserved\", \"source\": \"sim2real.netwatch\", " +
        "\"account\": \"123456789012\", \"time\": \"2026-02-03T11:22:33Z\", " +
        $"\"region\": \"{ctx.Region}\", \"resources\": [], \"detail\": {{\"ip\": \"{address}\"}}}}";

    /// <summary>Assert TestEventPattern's verdict, quoting the pattern and event that disagreed.</summary>
    private static async Task Expect(IAmazonEventBridge ev, string operand, string @event, bool want, string why)
    {
        var pattern = $"{{\"detail\": {{\"ip\": [{{\"cidr\": \"{operand}\"}}]}}}}";
        var got = (await ev.TestEventPatternAsync(new TestEventPatternRequest { EventPattern = pattern, Event = @event })).Result;
        Ensure(
            got == want,
            $"{why}: expected Result={Py(want)}, got {(got is { } g ? Py(g) : "None")}\n" +
            $"  pattern = {pattern}\n  event = {@event}");
    }

    private static string Py(bool value) => value ? "True" : "False";

    /// <summary><c>ipaddress.ip_network(operand, strict=False)</c>: addresses as integers.</summary>
    private sealed record Network(AddressFamily Family, int Bits, BigInteger First, BigInteger Last)
    {
        private int Width => Family == AddressFamily.InterNetwork ? 32 : 128;

        public static Network Of(string host, int bits)
        {
            var address = IPAddress.Parse(host);
            var width = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            var hostMask = (BigInteger.One << (width - bits)) - 1;
            var first = Value(address) & ~hostMask;
            return new Network(address.AddressFamily, bits, first, first | hostMask);
        }

        public static BigInteger Value(IPAddress address) => new(address.GetAddressBytes(), isUnsigned: true, isBigEndian: true);

        public bool Contains(BigInteger address) => address >= First && address <= Last;

        public string Format(BigInteger value)
        {
            var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
            var full = new byte[Width / 8];
            bytes.CopyTo(full, full.Length - bytes.Length);
            return new IPAddress(full).ToString();
        }

        public override string ToString() => $"{Format(First)}/{Bits}";
    }
}
