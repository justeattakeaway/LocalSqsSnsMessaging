// Translated from behavioral/sqs/check_fifo_batch_with_a_group_less_entry_rejected_whole_and_group_stream_unchanged.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-batch-with-a-group-less-entry-rejected-whole-and-group-stream-unchanged</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoBatchWithAGroupLessEntryRejectedWholeAndGroupStreamUnchanged
{
    private const string Check = "fifo-batch-with-a-group-less-entry-rejected-whole-and-group-stream-unchanged";

    private static readonly string[] Groups = ["red", "green", "blue"];
    private const int PerGroup = 3;
    private static readonly int Total = Groups.Length * PerGroup;
    private const string MissingGroupMsg = "The request must contain the parameter MessageGroupId.";

    /// <summary>
    /// A FIFO SendMessageBatch with one entry lacking MessageGroupId is refused whole with
    /// InvalidParameterValue and the exact message, enqueuing none of its valid siblings; the same
    /// batch with the group restored lands in full and every group drains in send order.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-batch-with-a-group-less-entry-rejected-whole-and-group-stream-unchanged")]
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
                ["ContentBasedDeduplication"] = "true",
                ["VisibilityTimeout"] = "120",
                ["ReceiveMessageWaitTimeSeconds"] = "0",
            },
            Tags = new() { ["sim2real-check"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        // Seed groups through a batch, entries interleaved across groups.
        var seed = Enumerable.Range(0, Total)
            .Select(i => new SendMessageBatchRequestEntry { Id = $"s{i}", MessageBody = $"{Groups[i % 3]}|{i / 3}", MessageGroupId = Groups[i % 3] })
            .ToList();
        foreach (var chunk in new[] { seed[..5], seed[5..] })
        {
            var ok0 = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = chunk });
            Ensure(ok0.Failed is null or { Count: 0 }, $"a valid seed batch had failed entries: {Repr(ok0.Failed)}");
        }

        await AwaitVisible(sqs, url, Total);

        // The refused batch's bodies differ from the corrected batch's, so content-based deduplication
        // can never hide a refused sibling that was enqueued anyway.
        var bad = new List<SendMessageBatchRequestEntry>
        {
            new() { Id = "b0", MessageBody = "red|refused", MessageGroupId = "red" },
            new() { Id = "b1", MessageBody = "green|refused" },
            new() { Id = "b2", MessageBody = "blue|refused", MessageGroupId = "blue" },
        };
        var response = await ErrorCode(() => sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = bad }));
        var got = (response.Code, response.Message);
        Ensure(got == ("InvalidParameterValue", MissingGroupMsg),
            $"a FIFO batch with a group-less entry was refused with {got}, " +
            $"expected {("InvalidParameterValue", MissingGroupMsg)}");
        var status = response.StatusCode;
        Ensure(status == HttpStatusCode.BadRequest, $"the batch was refused with HTTP {(int)status}, expected 400");
        await AwaitVisible(sqs, url, Total);

        var @fixed = bad.Zip(Groups)
            .Select(pair => new SendMessageBatchRequestEntry { Id = pair.First.Id, MessageBody = $"{pair.Second}|{PerGroup}", MessageGroupId = pair.Second })
            .ToList();
        var ok = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest { QueueUrl = url, Entries = @fixed });
        Ensure(ok.Failed is null or { Count: 0 } && ok.Successful!.Select(e => e.Id).Order(StringComparer.Ordinal).SequenceEqual(["b0", "b1", "b2"]),
            $"the corrected batch did not land in full: {Repr(ok)}");
        await AwaitVisible(sqs, url, Total + 3);

        var drained = new List<(string Body, string Group)>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed.TotalSeconds < 40.0 && drained.Count < Total + 3)
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
                Entries = [.. msgs.Select((m, i) => new DeleteMessageBatchRequestEntry { Id = $"d{i}", ReceiptHandle = m.ReceiptHandle })],
            });
        }

        var bodies = drained.Select(d => d.Body).Order(StringComparer.Ordinal).ToList();
        var leaked = bodies.Where(b => b.EndsWith("|refused", StringComparison.Ordinal)).ToList();
        Ensure(leaked.Count == 0, $"siblings of the refused batch were enqueued anyway: [{string.Join(", ", leaked)}]");
        Ensure(bodies.Count == Total + 3 && bodies.Distinct().Count() == Total + 3,
            $"drained [{string.Join(", ", bodies)}], expected {Total + 3} distinct messages (nothing from the refused batch)");
        var perGroup = new Dictionary<string, List<int>>();
        foreach (var (body, group) in drained)
        {
            var parts = body.Split('|');
            if (parts.Length != 2)
            {
                // Python's two-target unpacking of body.split("|") raises ValueError here.
                throw new InvalidOperationException($"cannot unpack {body} into (group, ordinal)");
            }

            var (bodyGroup, ordinal) = (parts[0], parts[1]);
            Ensure(bodyGroup == group,
                $"message {body} carried MessageGroupId {group}, not its own group");
            if (!perGroup.TryGetValue(group, out var list))
            {
                perGroup[group] = list = [];
            }

            list.Add(int.Parse(ordinal));
        }

        var expected = Groups.ToDictionary(g => g, _ => Enumerable.Range(0, PerGroup + 1).ToList());
        Ensure(perGroup.Count == expected.Count && expected.All(kv => perGroup.TryGetValue(kv.Key, out var v) && v.SequenceEqual(kv.Value)),
            $"per-group order is {Show(perGroup)}, expected {Show(expected)}");
    }

    /// <summary>Raised when a call AWS rejects is accepted.</summary>
    private sealed class UnexpectedSuccessException(string message) : Exception(message);

    /// <summary>The AWS error response (Error + ResponseMetadata), or a raise. Never a falsy sentinel.</summary>
    private static async Task<AwsError> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (resp, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            if (string.IsNullOrEmpty(error.Code))
            {
                throw new FidelityAssertionException(
                    $"call raised {error.Exception.GetType().Name} with no AWS error code: {error.Exception.Message}", error.Exception);
            }

            return error;
        }

        throw new UnexpectedSuccessException($"the call was ACCEPTED and returned {Repr(resp)}");
    }

    /// <summary>Poll ApproximateNumberOfMessages until it reads `want`, else fail with what we saw.</summary>
    private static async Task<string> AwaitVisible(AmazonSQSClient sqs, string url, int want)
    {
        var seen = "";
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed.TotalSeconds < 40.0)
        {
            seen = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!["ApproximateNumberOfMessages"];
            if (seen == want.ToString())
            {
                return seen;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        throw Fail($"queue never reported {want} visible messages; last read '{seen}'");
    }

    private static string Show(Dictionary<string, List<int>> d)
        => "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': [{string.Join(", ", kv.Value)}]")) + "}";

    private static string Repr(object? value)
    {
        try
        {
            return JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object));
        }
        catch (Exception)
        {
            return value?.ToString() ?? "None";
        }
    }
}
