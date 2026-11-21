// Translated from behavioral/sqs/check_receive_message_attribute_names_star_all_dot_star_select_the_same_set.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.receive-message-attribute-names-star-all-dot-star-select-the-same-set</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReceiveMessageAttributeNamesStarAllDotStarSelectTheSameSet
{
    private const string Check = "receive-message-attribute-names-star-all-dot-star-select-the-same-set";

    private static readonly Dictionary<string, (string DataType, string StringValue)> Attrs = new()
    {
        ["order.id"] = ("String", "A-1"),
        ["status"] = ("String", "open"),
    };

    /// <summary>
    /// MessageAttributeNames=['*'] projects every attribute with the send-time MD5, the same as
    /// 'All' and '.*'; a name prefix or an exact name projects only the matching subset, with
    /// MD5OfMessageAttributes recomputed over that subset rather than echoed from SendMessage.
    /// </summary>
    [Test]
    [DisplayName("sqs.receive-message-attribute-names-star-all-dot-star-select-the-same-set")]
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
        var fullDigest = Digest(Attrs.Keys);
        var prefixDigest = Digest(["order.id"]);
        var exactDigest = Digest(["status"]);
        Ensure(fullDigest != prefixDigest && fullDigest != exactDigest,
            "the full-set digest must differ from both single-attribute subset digests, or this " +
            $"check cannot discriminate them: full='{fullDigest}' prefix='{prefixDigest}' exact='{exactDigest}'");

        var sent = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url, MessageBody = "sim2real-attribute-projection",
            MessageAttributes = Attrs.ToDictionary(kv => kv.Key, kv => new MessageAttributeValue { DataType = kv.Value.DataType, StringValue = kv.Value.StringValue }),
        });
        var sentDigest = sent.MD5OfMessageAttributes;
        Ensure(sentDigest == fullDigest,
            $"SendMessage's MD5OfMessageAttributes '{sentDigest}' does not match the documented " +
            $"canonical digest over both sent attributes '{fullDigest}'");

        // --- '*', 'All' and '.*' each project the whole set with the send-time digest -----------
        foreach (var names in new List<string>[] { ["*"], ["All"], [".*"] })
        {
            var m = await ReceiveProjected(sqs, url, names, pollSeconds);
            Ensure(m.MessageId == sent.MessageId, $"MessageAttributeNames={Show(names)} returned a different message: '{m.MessageId}'");
            var g = m.MessageAttributes ?? [];
            Ensure(g.Keys.ToHashSet().SetEquals(Attrs.Keys),
                $"MessageAttributeNames={Show(names)} returned {Show(g.Keys)}, expected {Show(Attrs.Keys)}");
            foreach (var (name, spec) in Attrs)
            {
                Ensure(g[name].StringValue == spec.StringValue,
                    $"attribute '{name}' under {Show(names)} carried '{g[name].StringValue}', expected '{spec.StringValue}'");
            }

            var gotDigest = m.MD5OfMessageAttributes;
            Ensure(gotDigest == fullDigest && fullDigest == sentDigest,
                $"MessageAttributeNames={Show(names)} returned digest '{gotDigest}'; expected it to " +
                $"equal both the recomputed full-set digest '{fullDigest}' and SendMessage's own digest '{sentDigest}'");
        }

        // --- a name prefix selects only the matching attribute, digest recomputed over it --------
        var msg = await ReceiveProjected(sqs, url, ["order.*"], pollSeconds);
        Ensure(msg.MessageId == sent.MessageId, $"MessageAttributeNames=['order.*'] returned a different message: '{msg.MessageId}'");
        var got = msg.MessageAttributes ?? [];
        Ensure(got.Keys.ToHashSet().SetEquals(["order.id"]),
            $"MessageAttributeNames=['order.*'] returned {Show(got.Keys)}, expected ['order.id']");
        Ensure(got["order.id"].StringValue == Attrs["order.id"].StringValue,
            $"order.id carried '{got["order.id"].StringValue}', expected '{Attrs["order.id"].StringValue}'");
        var prefixGotDigest = msg.MD5OfMessageAttributes;
        Ensure(prefixGotDigest == prefixDigest,
            $"MessageAttributeNames=['order.*'] returned digest '{prefixGotDigest}', expected the " +
            $"digest recomputed over the one-attribute subset '{prefixDigest}'");
        Ensure(prefixGotDigest != sentDigest,
            $"the prefix projection's digest equals SendMessage's full-set digest '{sentDigest}'; " +
            "it must be recomputed over the projected subset, not echoed from SendMessage");

        // --- an exact name selects only that attribute, digest recomputed over it ----------------
        msg = await ReceiveProjected(sqs, url, ["status"], pollSeconds);
        Ensure(msg.MessageId == sent.MessageId, $"MessageAttributeNames=['status'] returned a different message: '{msg.MessageId}'");
        got = msg.MessageAttributes ?? [];
        Ensure(got.Keys.ToHashSet().SetEquals(["status"]),
            $"MessageAttributeNames=['status'] returned {Show(got.Keys)}, expected ['status']");
        Ensure(got["status"].StringValue == Attrs["status"].StringValue,
            $"status carried '{got["status"].StringValue}', expected '{Attrs["status"].StringValue}'");
        var exactGotDigest = msg.MD5OfMessageAttributes;
        Ensure(exactGotDigest == exactDigest,
            $"MessageAttributeNames=['status'] returned digest '{exactGotDigest}', expected the " +
            $"digest recomputed over the one-attribute subset '{exactDigest}'");
        Ensure(exactGotDigest != sentDigest,
            $"the exact-name projection's digest equals SendMessage's full-set digest '{sentDigest}'; " +
            "it must be recomputed over the projected subset, not echoed from SendMessage");
        await sqs.DeleteMessageAsync(url, msg.ReceiptHandle);
    }

    private static string Show(IEnumerable<string> names) =>
        "[" + string.Join(", ", names.Order(StringComparer.Ordinal).Select(n => $"'{n}'")) + "]";

    /// <summary>
    /// The documented MD5 over an attribute map: sorted names, length-prefixed fields, one hash.
    /// https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-message-metadata.html
    /// </summary>
    private static string Digest(IEnumerable<string> names)
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

    /// <summary>Receive the one message under <paramref name="names"/>, reset its visibility, and return it.</summary>
    private static async Task<Message> ReceiveProjected(AmazonSQSClient sqs, string url, List<string> names, double timeoutSeconds)
    {
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 2, VisibilityTimeout = 0, MessageAttributeNames = names,
            })).Messages ?? [];
            if (msgs.Count > 0)
            {
                return msgs[0];
            }
        }

        throw Fail($"no message was receivable within {timeoutSeconds}s for names={Show(names)}");
    }
}
