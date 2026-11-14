// Translated from behavioral/sqs/check_sqsbatch_send_partial_failure_preserves_survivor_attributes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.sqsbatch-send-partial-failure-preserves-survivor-attributes</summary>
[Category("AwsEvaluationSuite")]
public sealed class SqsbatchSendPartialFailurePreservesSurvivorAttributes
{
    private const string ErrEmptyValue = "EmptyValue";

    private const int BatchMaxEntries = 10;
    private const int BadIndex = 4;

    /// <summary>
    /// When one of ten SendMessageBatch entries fails, the nine survivors each get their own
    /// MD5OfMessageAttributes and come back with their String/Number/Binary attributes byte-exact,
    /// and the rejected entry's attributes are nowhere on the queue.
    /// </summary>
    [Test]
    [DisplayName("sqs.sqsbatch-send-partial-failure-preserves-survivor-attributes")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Batch, "an empty-bodied entry is reported Successful; all ten Ids came back Successful instead of the nine with a non-empty body")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        const int observeS = 16;

        var bodies = Enumerable.Range(0, BatchMaxEntries).ToDictionary(i => i, i => $"attr-survivor-{i}");
        var ids = Enumerable.Range(0, BatchMaxEntries).Select(i => $"e{i}").ToList();
        var entries = Enumerable.Range(0, BatchMaxEntries)
            .Select(i => new SendMessageBatchRequestEntry
            {
                Id = ids[i],
                MessageBody = i == BadIndex ? "" : bodies[i],
                MessageAttributes = Attributes(i),
            })
            .ToList();
        var survivors = Enumerable.Range(0, BatchMaxEntries).Where(i => i != BadIndex).ToList();

        var resp = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = entries });
        var succ = (resp.Successful ?? []).ToDictionary(e => e.Id, StringComparer.Ordinal);
        var fail = (resp.Failed ?? []).ToDictionary(e => e.Id, StringComparer.Ordinal);

        Ensure(succ.Keys.ToHashSet().SetEquals(survivors.Select(i => ids[i])),
            $"Successful Ids {Sorted(succ.Keys)} != the nine entries with a non-empty body " +
            $"{Sorted(survivors.Select(i => ids[i]))}");
        Ensure(fail.Keys.ToHashSet().SetEquals([ids[BadIndex]]),
            $"Failed Ids {Sorted(fail.Keys)}, expected exactly ['{ids[BadIndex]}']");
        Ensure(fail[ids[BadIndex]].Code == ErrEmptyValue,
            $"Failed Code is '{fail[ids[BadIndex]].Code}', expected '{ErrEmptyValue}'");
        Ensure(fail[ids[BadIndex]].Message == $"No Value Found for SendMessageBatchRequestEntry.{BadIndex + 1}.MessageBody.",
            $"Failed Message is '{fail[ids[BadIndex]].Message}'");

        // Every survivor got its OWN attribute digest, and the digests differ because the
        // attributes differ -- a target that computes one digest for the batch, or reuses the body
        // digest, diverges here.
        var attrMd5 = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var i in survivors)
        {
            var entry = succ[ids[i]];
            var digest = entry.MD5OfMessageAttributes ?? "";
            Ensure(digest.Length == 32 && digest.All(c => "0123456789abcdef".Contains(c, StringComparison.Ordinal)),
                $"Successful['{ids[i]}'].MD5OfMessageAttributes is not a 32-char hex digest: '{digest}'");
            Ensure(digest != entry.MD5OfMessageBody,
                $"Successful['{ids[i]}'] reused MD5OfMessageBody as MD5OfMessageAttributes: '{digest}'");
            attrMd5[ids[i]] = digest;
        }

        Ensure(attrMd5.Values.Distinct().Count() == survivors.Count,
            $"the nine survivors produced {attrMd5.Values.Distinct().Count()} distinct attribute digests, " +
            $"expected {survivors.Count}: {{{string.Join(", ", attrMd5.Select(kv => $"'{kv.Key}': '{kv.Value}'"))}}}");

        // --- the effect, read back off the data plane ------------------------------------------
        var got = await ObserveWithAttributes(sqs, url, timeoutSeconds: observeS);
        Ensure(got.Keys.ToHashSet().SetEquals(survivors.Select(i => bodies[i])),
            $"received bodies {Sorted(got.Keys)}, expected exactly the nine survivors " +
            $"{Sorted(survivors.Select(i => bodies[i]))}");
        foreach (var i in survivors)
        {
            var want = Attributes(i);
            var have = got[bodies[i]];
            Ensure(have.Keys.ToHashSet().SetEquals(want.Keys),
                $"'{bodies[i]}' came back with attribute names {Sorted(have.Keys)}, expected " +
                $"{Sorted(want.Keys)}");
            Ensure(have["idx"].DataType == "Number" && have["idx"].StringValue == i.ToString(CultureInfo.InvariantCulture),
                $"'{bodies[i]}' Number attribute came back as {Show(have["idx"])}, expected " +
                $"{Show(want["idx"])}");
            Ensure(have["tag"].DataType == "String" && have["tag"].StringValue == want["tag"].StringValue,
                $"'{bodies[i]}' String attribute came back as {Show(have["tag"])}, expected " +
                $"{Show(want["tag"])}");
            Ensure(have["blob"].DataType == "Binary",
                $"'{bodies[i]}' blob attribute DataType came back as " +
                $"'{have["blob"].DataType}', expected 'Binary'");
            // botocore decodes the transport's base64 for us, so this compares raw bytes to raw
            // bytes -- a target that returns the encoded form fails here rather than silently.
            Ensure(have["blob"].BinaryValue!.ToArray().AsSpan().SequenceEqual(want["blob"].BinaryValue.ToArray()),
                $"'{bodies[i]}' Binary attribute came back as " +
                $"{Convert.ToHexString(have["blob"].BinaryValue!.ToArray())}, expected " +
                $"{Convert.ToHexString(want["blob"].BinaryValue.ToArray())}");
        }

        // --- effect absent: the rejected entry's attributes never reached the queue -------------
        var seenIdx = got.Values.Select(a => int.Parse(a["idx"].StringValue, CultureInfo.InvariantCulture)).ToHashSet();
        Ensure(seenIdx.SetEquals(survivors),
            $"attribute idx values on the queue are [{string.Join(", ", seenIdx.Order())}], expected [{string.Join(", ", survivors)}]; " +
            $"idx={BadIndex} belongs to the entry AWS reported Failed and must be absent");
        var remaining = await Poll(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!["ApproximateNumberOfMessages"],
            v => v == "0",
            timeoutSeconds: 30);
        Ensure(remaining == "0", $"queue still reports '{remaining}' visible messages after all nine were drained");
    }

    /// <summary>
    /// Three MessageAttributes per entry, one of each data type, all keyed off i so the
    /// per-entry digests differ and a cross-entry mix-up is visible.
    /// </summary>
    private static Dictionary<string, MessageAttributeValue> Attributes(int i) => new()
    {
        ["idx"] = new MessageAttributeValue { DataType = "Number", StringValue = i.ToString(CultureInfo.InvariantCulture) },
        ["tag"] = new MessageAttributeValue { DataType = "String", StringValue = $"survivor-tag-{i}" },
        ["blob"] = new MessageAttributeValue { DataType = "Binary", BinaryValue = new MemoryStream([(byte)i, (byte)(i + 1), (byte)(i + 2)]) },
    };

    private static string Show(MessageAttributeValue v)
        => $"{{'DataType': '{v.DataType}', 'StringValue': '{v.StringValue}'}}";

    private static string Sorted(IEnumerable<string> ids) => "[" + string.Join(", ", ids.Order(StringComparer.Ordinal).Select(i => $"'{i}'")) + "]";

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutSeconds, double intervalSeconds = 0.5)
    {
        var deadline = Stopwatch.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                return val;
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
            val = await fn();
        }

        return val;
    }

    /// <summary>
    /// {body: MessageAttributes} for every distinct message visible inside timeout_s, each
    /// deleted so it is counted once. Always runs the full window: the window closing with
    /// nothing further arriving is the effect-absent evidence.
    /// </summary>
    private static async Task<Dictionary<string, Dictionary<string, MessageAttributeValue>>> ObserveWithAttributes(AmazonSQSClient sqs, string url, double timeoutSeconds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new Dictionary<string, Dictionary<string, MessageAttributeValue>>(StringComparer.Ordinal);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 1,
                MessageAttributeNames = ["All"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                if (seen.Add(msg.MessageId))
                {
                    output[msg.Body] = msg.MessageAttributes ?? [];
                }

                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        return output;
    }
}
