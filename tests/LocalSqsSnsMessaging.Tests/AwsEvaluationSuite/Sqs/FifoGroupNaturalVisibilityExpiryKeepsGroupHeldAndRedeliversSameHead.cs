// Translated from behavioral/sqs/check_fifo_group_natural_visibility_expiry_keeps_group_held_and_redelivers_same_head.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-group-natural-visibility-expiry-keeps-group-held-and-redelivers-same-head</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoGroupNaturalVisibilityExpiryKeepsGroupHeldAndRedeliversSameHead
{
    // The visibility timeout is left to lapse on its own; no ChangeMessageVisibility(0) handoff.
    private const int VisibilityS = 5;

    private static readonly Dictionary<string, string> Tags = new()
    {
        ["sim2real:managed"] = "true",
        ["sim2real"] = "fifo-group-natural-vt-expiry",
    };

    /// <summary>
    /// An undeleted FIFO head message holds its group until its visibility timeout lapses naturally.
    ///
    /// The group then redelivers the SAME head (same MessageId), and the rest drain in send order.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-group-natural-visibility-expiry-keeps-group-held-and-redelivers-same-head")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [Category(ApplyDefaultCategoryAttribute.TimeBasedCategoryName)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true", ["VisibilityTimeout"] = VisibilityS.ToString(CultureInfo.InvariantCulture) },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var token = Guid.NewGuid().ToString("N")[..8];
        const string group = "sim2real-group";
        var bodies = Enumerable.Range(0, 3).Select(i => $"{token}-{i}").ToList();
        for (var i = 0; i < bodies.Count; i++)
        {
            await sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = url, MessageBody = bodies[i], MessageGroupId = group,
                MessageDeduplicationId = $"{token}-{i}",
            });
        }

        var first = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 2 })).Messages ?? [];
        Ensure(first.Count == 1 && first[0].Body == bodies[0],
            $"expected the group head '{bodies[0]}' alone, got {Repr(first)}");
        var headId = first[0].MessageId;

        var held = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 })).Messages ?? [];
        Ensure(held.Count == 0,
            $"group must stay held while its head is in flight, but a second poll returned {Repr(held)}");

        // MaxNumberOfMessages=1: a larger batch could also return the head's queued successors once
        // the group reopens; one at a time isolates the head's own redelivery.
        List<Message> afterExpiry = [];
        double waitS = VisibilityS + 30;
        var left = ctx.TimeLeftSeconds;
        var deadlineSeconds = Math.Min(waitS, left);
        var clock = FidelityTime.StartNew();
        while (afterExpiry.Count == 0 && clock.Elapsed.TotalSeconds < deadlineSeconds)
        {
            afterExpiry = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 1 })).Messages ?? [];
            if (afterExpiry.Count == 0)
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(1));
            }
        }

        Ensure(afterExpiry.Count == 1 && afterExpiry[0].Body == bodies[0],
            $"after the visibility timeout lapsed naturally, expected the SAME head '{bodies[0]}' " +
            $"redelivered alone, got {Repr(afterExpiry)}");
        Ensure(afterExpiry[0].MessageId == headId,
            $"redelivery after natural expiry minted a different MessageId " +
            $"'{afterExpiry[0].MessageId}' for what must be the same message '{headId}'");

        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = afterExpiry[0].ReceiptHandle });
        List<string> rest = [];
        HashSet<string> seen = [];
        var idle = 0;
        for (var r = 0; r < 10; r++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
            var msgs = resp.Messages ?? [];
            if (msgs.Count == 0)
            {
                idle++;
                if (rest.Count > 0 && idle >= 2)
                {
                    break;
                }

                continue;
            }

            idle = 0;
            foreach (var m in msgs)
            {
                if (seen.Contains(m.MessageId))
                {
                    continue;
                }

                seen.Add(m.MessageId);
                rest.Add(m.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(rest.SequenceEqual(bodies[1..]),
            $"after draining the head, expected the group's remaining order [{string.Join(", ", bodies[1..])}], " +
            $"got [{string.Join(", ", rest)}]");
    }

    private static string Repr(List<Message> messages) =>
        "[" + string.Join(", ", messages.Select(m => $"{{MessageId: '{m.MessageId}', Body: '{m.Body}'}}")) + "]";
}
