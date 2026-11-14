// Translated from behavioral/sqs/check_fifo_send_without_message_group_id_rejected_and_group_stream_unchanged.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-send-without-message-group-id-rejected-and-group-stream-unchanged</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoSendWithoutMessageGroupIdRejectedAndGroupStreamUnchanged
{
    private const string Check = "fifo-send-without-message-group-id-rejected-and-group-stream-unchanged";

    private static readonly string[] Groups = ["g0", "g1", "g2", "g3"];
    private const int PerGroup = 5;
    private static readonly int Total = Groups.Length * PerGroup;

    /// <summary>
    /// A FIFO send with no MessageGroupId is rejected (MissingParameter) and enqueues nothing.
    ///
    /// The queued messages still drain in per-group order, and a valid follow-up send lands next.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-send-without-message-group-id-rejected-and-group-stream-unchanged")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-d5-") + ".fifo",
            Attributes = new()
            {
                ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "true",
                ["VisibilityTimeout"] = "120", ["ReceiveMessageWaitTimeSeconds"] = "0",
            },
            Tags = new() { ["sim2real-check"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        // Interleaved so per-group order differs from send order.
        for (var i = 0; i < Total; i++)
        {
            var group = Groups[i % Groups.Length];
            await sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = url, MessageBody = $"{group}|{i / Groups.Length}",
                MessageGroupId = group,
            });
        }

        await AwaitVisible(sqs, url, Total);

        const string rejectedBody = "orphan|no-group";
        var response = await ErrorCode(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = rejectedBody }));
        var code = response.Code;
        Ensure(code == "MissingParameter",
            $"a FIFO SendMessage with no MessageGroupId was rejected with '{code}', " +
            "expected 'MissingParameter'");
        var status = (int)response.StatusCode;
        Ensure(status is >= 400 and < 500, $"the group-less send was rejected with HTTP {status}, expected 4xx");
        var message = response.Message ?? "";
        Ensure(!string.IsNullOrWhiteSpace(message), "expected a non-empty MissingParameter message");

        await AwaitVisible(sqs, url, Total);

        var controlBody = $"{Groups[0]}|{PerGroup}";
        var sent = await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = controlBody, MessageGroupId = Groups[0] });
        Ensure(!string.IsNullOrEmpty(sent.SequenceNumber ?? throw new KeyNotFoundException("SequenceNumber")),
            $"the control send returned no SequenceNumber: MessageId={sent.MessageId}");
        await AwaitVisible(sqs, url, Total + 1);

        List<(string Body, string Group)> drained = [];
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(120) && drained.Count < Total + 1)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5,
                MessageSystemAttributeNames = ["MessageGroupId"],
            });
            var msgs = resp.Messages ?? [];
            if (msgs.Count == 0)
            {
                continue;
            }

            foreach (var m in msgs)
            {
                drained.Add((m.Body, m.Attributes!["MessageGroupId"]));
            }

            await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
            {
                QueueUrl = url,
                Entries = msgs.Select((m, i) => new DeleteMessageBatchRequestEntry { Id = $"d{i}", ReceiptHandle = m.ReceiptHandle }).ToList(),
            });
        }

        var bodies = drained.Select(d => d.Body).ToList();
        Ensure(!bodies.Contains(rejectedBody),
            $"the rejected group-less message was delivered anyway, as '{drained.LastOrDefault(d => d.Body == rejectedBody).Group}'");
        Ensure(bodies.Contains(controlBody), $"the control message never arrived; drained [{string.Join(", ", bodies.Order(StringComparer.Ordinal))}]");
        Ensure(bodies.Count == Total + 1,
            $"drained {bodies.Count} messages, expected {Total + 1} " +
            $"({Total} setup + 1 control, 0 from the rejected send): [{string.Join(", ", bodies.Order(StringComparer.Ordinal))}]");

        var perGroup = new Dictionary<string, List<int>>();
        foreach (var (body, group) in drained)
        {
            var parts = body.Split('|');
            var (bodyGroup, ordinal) = (parts[0], parts[1]);
            Ensure(bodyGroup == group,
                $"message '{body}' carried MessageGroupId '{group}', not its own group");
            if (!perGroup.TryGetValue(group, out var list))
            {
                perGroup[group] = list = [];
            }

            list.Add(int.Parse(ordinal, CultureInfo.InvariantCulture));
        }

        var expected = Groups.ToDictionary(g => g, g => Enumerable.Range(0, PerGroup + (g == Groups[0] ? 1 : 0)).ToList());
        Ensure(perGroup.Count == expected.Count && expected.All(kv => perGroup.TryGetValue(kv.Key, out var v) && v.SequenceEqual(kv.Value)),
            $"per-group order after the rejection is {Repr(perGroup)}, expected {Repr(expected)}");
    }

    /// <summary>The AWS error response (Error + ResponseMetadata), or a raise. Never a falsy sentinel.</summary>
    private static async Task<AwsError> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (resp, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw new InvalidOperationException($"the call was ACCEPTED and returned {resp}");
        }

        if (string.IsNullOrEmpty(error.Code))
        {
            throw Fail($"call raised {error.Exception.GetType().Name} with no AWS error code: {error.Exception.Message}");
        }

        return error;
    }

    /// <summary>Poll ApproximateNumberOfMessages until it reads `want`, else fail with what we saw.</summary>
    private static async Task<string> AwaitVisible(AmazonSQSClient sqs, string url, int want)
    {
        var seen = "";
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            seen = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url, AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!["ApproximateNumberOfMessages"];
            if (seen == want.ToString(CultureInfo.InvariantCulture))
            {
                return seen;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"queue never reported {want} visible messages; last read '{seen}'");
    }

    private static string Repr(Dictionary<string, List<int>> d) =>
        "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': [{string.Join(", ", kv.Value)}]")) + "}";
}
