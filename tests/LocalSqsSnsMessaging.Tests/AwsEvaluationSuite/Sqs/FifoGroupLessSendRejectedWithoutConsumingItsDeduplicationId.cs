// Translated from behavioral/sqs/check_fifo_group_less_send_rejected_without_consuming_its_deduplication_id.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-group-less-send-rejected-without-consuming-its-deduplication-id</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoGroupLessSendRejectedWithoutConsumingItsDeduplicationId
{
    private const string Check = "fifo-group-less-send-rejected-without-consuming-its-deduplication-id";

    // Unequal group lengths, interleaved, so per-group order differs from send order.
    private static readonly (string Group, int Ordinal)[] Sends =
    [
        ("alpha", 0),
        ("beta", 0),
        ("beta", 1),
        ("gamma", 0),
        ("beta", 2),
        ("alpha", 1),
        ("beta", 3),
    ];

    private const string ReusedDedupId = "dedup-reused-by-the-rejected-send";
    private const string MissingGroupMsg = "The request must contain the parameter MessageGroupId.";

    /// <summary>
    /// On a FIFO queue without content-based deduplication, a send with a MessageDeduplicationId but
    /// no MessageGroupId is refused (MissingParameter, exact message) and does not consume that id: a
    /// valid send reusing it is enqueued, a repeat of that valid send is deduplicated, groups keep order.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-group-less-send-rejected-without-consuming-its-deduplication-id")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-d5-") + ".fifo",
            Attributes = new()
            {
                ["FifoQueue"] = "true",
                ["ContentBasedDeduplication"] = "false",
                ["VisibilityTimeout"] = "120",
                ["ReceiveMessageWaitTimeSeconds"] = "0",
            },
            Tags = new() { ["sim2real-check"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        for (var n = 0; n < Sends.Length; n++)
        {
            var (group, ordinal) = Sends[n];
            await sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = url,
                MessageBody = $"{group}|{ordinal}",
                MessageGroupId = group,
                MessageDeduplicationId = $"seed-{n}",
            });
        }

        await AwaitVisible(sqs, url, Sends.Length);

        const string rejectedBody = "orphan|no-group";
        var response = await ErrorCode(() => sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = rejectedBody,
            MessageDeduplicationId = ReusedDedupId,
        }));
        var got = (response.Code, response.Message);
        Ensure(got == ("MissingParameter", MissingGroupMsg),
            $"a FIFO SendMessage with a dedup id but no MessageGroupId was rejected with {got}, " +
            $"expected {("MissingParameter", MissingGroupMsg)}");
        var status = (int)response.StatusCode;
        Ensure(status == 400, $"the group-less send was rejected with HTTP {status}, expected 400");
        await AwaitVisible(sqs, url, Sends.Length);

        // The rejected send's dedup id was never recorded, so a valid send carrying it is enqueued.
        const string reuseBody = "gamma|1";
        var first = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = reuseBody,
            MessageGroupId = "gamma",
            MessageDeduplicationId = ReusedDedupId,
        });
        Ensure(!string.IsNullOrEmpty(first.SequenceNumber ?? throw new KeyNotFoundException("SequenceNumber")),
            $"the valid send returned no SequenceNumber: MessageId={first.MessageId}");
        await AwaitVisible(sqs, url, Sends.Length + 1);
        // Positive control: the id is live now, so an identical send inside the window is deduplicated.
        var repeat = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = reuseBody,
            MessageGroupId = "gamma",
            MessageDeduplicationId = ReusedDedupId,
        });
        Ensure(repeat.MessageId == first.MessageId,
            $"a duplicate send returned MessageId '{repeat.MessageId}', expected the original " +
            $"'{first.MessageId}'");
        await AwaitVisible(sqs, url, Sends.Length + 1);

        var total = Sends.Length + 1;
        List<(string Body, string Group)> drained = [];
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(40.0) && drained.Count < total)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
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
        Ensure(!bodies.Contains(rejectedBody), "the rejected group-less message was delivered anyway");
        Ensure(bodies.Count == total && bodies.Count(b => b == reuseBody) == 1,
            $"drained [{string.Join(", ", bodies.Order(StringComparer.Ordinal))}], expected {total} messages with '{reuseBody}' exactly once");
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

        var expected = new Dictionary<string, List<int>> { ["alpha"] = [0, 1], ["beta"] = [0, 1, 2, 3], ["gamma"] = [0, 1] };
        Ensure(perGroup.Count == expected.Count && expected.All(kv => perGroup.TryGetValue(kv.Key, out var v) && v.SequenceEqual(kv.Value)),
            $"per-group order is {Repr(perGroup)}, expected {Repr(expected)}");
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
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(40.0))
        {
            seen = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!["ApproximateNumberOfMessages"];
            if (seen == want.ToString(CultureInfo.InvariantCulture))
            {
                return seen;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"queue never reported {want} visible messages; last read '{seen}'");
    }

    private static string Repr(Dictionary<string, List<int>> d) =>
        "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': [{string.Join(", ", kv.Value)}]")) + "}";
}
