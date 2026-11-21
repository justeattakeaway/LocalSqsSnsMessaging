// Translated from behavioral/sqs/check_wildcard_attribute_filter_subset_digest_contrasts_full_digest.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.wildcard-attribute-filter-subset-digest-contrasts-full-digest</summary>
[Category("AwsEvaluationSuite")]
public sealed class WildcardAttributeFilterSubsetDigestContrastsFullDigest
{
    private const string Check = "wildcard-attribute-filter-subset-digest-contrasts-full-digest";

    private static readonly Dictionary<string, (string DataType, string StringValue)> Attrs = new()
    {
        ["color.primary"] = ("String", "crimson"),
        ["color"] = ("String", "red"),
        ["colorful"] = ("String", "yes"),
        ["secret"] = ("String", "do-not-project"),
    };

    private static readonly HashSet<string> WildcardSubset = ["color.primary", "color", "colorful"];

    /// <summary>
    /// A <c>color.*</c> projection returns exactly the three matching attributes with
    /// MD5OfMessageAttributes recomputed over that subset, distinct from the full-set digest; after a
    /// visibility reset, projecting <c>All</c> returns all four with the digest SendMessage reported.
    /// </summary>
    [Test]
    [DisplayName("sqs.wildcard-attribute-filter-subset-digest-contrasts-full-digest")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-sqs-"), Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        const double pollSeconds = 30.0;
        var fullDigest = AttributeDigest(Attrs.Keys);
        var subsetDigest = AttributeDigest(WildcardSubset);
        Ensure(subsetDigest != fullDigest,
            $"the subset and full digests must differ, or this check cannot discriminate them: both are '{fullDigest}'");

        var sent = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url, MessageBody = "wildcard-digest-subject",
            MessageAttributes = Attrs.ToDictionary(kv => kv.Key, kv => new MessageAttributeValue { DataType = kv.Value.DataType, StringValue = kv.Value.StringValue }),
        });
        var sentDigest = sent.MD5OfMessageAttributes;
        Ensure(sentDigest == fullDigest,
            $"SendMessage's MD5OfMessageAttributes '{sentDigest}' does not match the documented " +
            $"canonical digest over all four sent attributes '{fullDigest}'");

        // --- (a) the color.* projection: exactly the three-member subset, subset digest ----------
        var msg = await PollReceive(sqs, url, pollSeconds, ["color.*"]);
        Ensure(msg.MessageId == sent.MessageId,
            $"the color.* receive returned a different message than the one sent: '{msg.MessageId}'");
        var got = msg.MessageAttributes ?? [];
        Ensure(got.Keys.ToHashSet().SetEquals(WildcardSubset),
            $"MessageAttributeNames=['color.*'] returned {Show(got.Keys)}, expected {Show(WildcardSubset)}");
        foreach (var name in WildcardSubset)
        {
            Ensure(got[name].StringValue == Attrs[name].StringValue,
                $"attribute '{name}' carried value '{got[name].StringValue}', expected '{Attrs[name].StringValue}'");
        }

        var subsetHandle = msg.ReceiptHandle;
        var gotDigest = msg.MD5OfMessageAttributes;
        Ensure(gotDigest == subsetDigest,
            $"the color.* projection's MD5OfMessageAttributes is '{gotDigest}', expected the digest " +
            $"recomputed over just the three-member subset '{subsetDigest}'");
        // The strong half of the claim: a server that merely echoes SendMessage's own digest, rather
        // than recomputing it over the projected subset, must fail here.
        Ensure(gotDigest != fullDigest,
            $"the color.* projection's digest equals the full four-attribute digest '{fullDigest}'; " +
            "it must be recomputed over the three-member projected subset, not echoed from SendMessage");

        // --- (b) reset visibility, then project All: all four, digest equal to SendMessage's ----
        await sqs.ChangeMessageVisibilityAsync(url, subsetHandle, 0);
        var msgAll = await PollReceive(sqs, url, pollSeconds, ["All"]);
        Ensure(msgAll.MessageId == sent.MessageId,
            $"the All receive after the visibility reset returned a different message: '{msgAll.MessageId}'");
        var gotAll = msgAll.MessageAttributes ?? [];
        Ensure(gotAll.Keys.ToHashSet().SetEquals(Attrs.Keys),
            $"MessageAttributeNames=['All'] returned {Show(gotAll.Keys)}, expected {Show(Attrs.Keys)}");
        foreach (var (name, spec) in Attrs)
        {
            Ensure(gotAll[name].StringValue == spec.StringValue,
                $"attribute '{name}' under All carried '{gotAll[name].StringValue}', expected '{spec.StringValue}'");
        }

        var allDigest = msgAll.MD5OfMessageAttributes;
        Ensure(allDigest == fullDigest && fullDigest == sentDigest,
            $"the All projection's MD5OfMessageAttributes is '{allDigest}'; expected it to equal both " +
            $"the recomputed full-set digest '{fullDigest}' and SendMessage's own digest '{sentDigest}'");
        await sqs.DeleteMessageAsync(url, msgAll.ReceiptHandle);
    }

    private static string Show(IEnumerable<string> names) =>
        "[" + string.Join(", ", names.Order(StringComparer.Ordinal).Select(n => $"'{n}'")) + "]";

    /// <summary>
    /// The documented MD5 over a whole attribute map: sort by name, concatenate, hash once.
    /// https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-message-metadata.html
    /// </summary>
    private static string AttributeDigest(IEnumerable<string> names)
    {
        var buf = new List<byte>();
        foreach (var name in names.Order(StringComparer.Ordinal))
        {
            var spec = Attrs[name];
            AddPrefixed(buf, Encoding.UTF8.GetBytes(name));
            AddPrefixed(buf, Encoding.UTF8.GetBytes(spec.DataType));
            buf.Add(0x01);
            AddPrefixed(buf, Encoding.UTF8.GetBytes(spec.StringValue));
        }

        return Convert.ToHexStringLower(MD5.HashData([.. buf]));
    }

    private static void AddPrefixed(List<byte> buf, byte[] raw)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)raw.Length);
        buf.AddRange(length);
        buf.AddRange(raw);
    }

    /// <summary>Bounded poll for a single message; raises on the deadline.</summary>
    private static async Task<Message> PollReceive(AmazonSQSClient sqs, string url, double timeoutSeconds, List<string> messageAttributeNames)
    {
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 2, MessageAttributeNames = messageAttributeNames,
            })).Messages ?? [];
            if (msgs.Count > 0)
            {
                return msgs[0];
            }
        }

        throw Fail($"no message received within {timeoutSeconds}s for kwargs {{'MessageAttributeNames': {Show(messageAttributeNames)}}}");
    }
}
