// Translated from behavioral/sqs/check_fifo_group_held_for_the_extended_visibility_timeout_not_the_original.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-group-held-for-the-extended-visibility-timeout-not-the-original</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoGroupHeldForTheExtendedVisibilityTimeoutNotTheOriginal
{
    private const int InitialVisibilityS = 4;
    private const int ExtendedVisibilityS = 10;

    private static readonly Dictionary<string, string> Tags = new()
    {
        ["sim2real:managed"] = "true",
        ["sim2real"] = "fifo-group-held-extended-vt",
    };

    /// <summary>
    /// ChangeMessageVisibility on an in-flight FIFO head message re-arms the group lock: the group
    /// stays held past the original timeout and opens, returning the same head, only after the extended
    /// one.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-group-held-for-the-extended-visibility-timeout-not-the-original")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [Category(ApplyDefaultCategoryAttribute.TimeBasedCategoryName)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true", ["VisibilityTimeout"] = InitialVisibilityS.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            Tags = new(Tags),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var token = Guid.NewGuid().ToString("N")[..8];
        const string group = "sim2real-group";
        var bodies = Enumerable.Range(0, 2).Select(i => $"{token}-{i}").ToList();
        for (var i = 0; i < bodies.Count; i++)
        {
            await sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = url, MessageBody = bodies[i], MessageGroupId = group,
                MessageDeduplicationId = $"{token}-{i}",
            });
        }

        var head = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 2 })).Messages![0];
        Ensure(head.Body == bodies[0], $"expected the group head '{bodies[0]}', got '{head.Body}'");

        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = url, ReceiptHandle = head.ReceiptHandle, VisibilityTimeout = ExtendedVisibilityS,
        });

        // Past the ORIGINAL timeout, but still inside the EXTENDED one: the group must stay held.
        await FidelityTime.Delay(TimeSpan.FromSeconds(InitialVisibilityS + 2));
        var stillHeld = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 })).Messages ?? [];
        Ensure(stillHeld.Count == 0,
            $"group released after the ORIGINAL {InitialVisibilityS}s timeout despite the " +
            $"extension to {ExtendedVisibilityS}s: got {Repr(stillHeld)}");

        // Past the EXTENDED timeout now: the group must release, and give back the SAME head.
        // MaxNumberOfMessages=1: a larger batch can return several already-queued members of one
        // FIFO group in a single call once the group reopens, which would not isolate the head's
        // own redelivery from its successor's first delivery.
        List<Message> released = [];
        double waitS = ExtendedVisibilityS + 30;
        var left = ctx.TimeLeftSeconds;
        var deadlineSeconds = Math.Min(waitS, left);
        var clock = FidelityTime.StartNew();
        while (released.Count == 0 && clock.Elapsed.TotalSeconds < deadlineSeconds)
        {
            released = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 1 })).Messages ?? [];
            if (released.Count == 0)
            {
                await FidelityTime.Delay(TimeSpan.FromSeconds(1));
            }
        }

        Ensure(released.Count == 1 && released[0].Body == bodies[0],
            $"expected the same head '{bodies[0]}' redelivered once the extended timeout elapsed, " +
            $"got {Repr(released)}");
        Ensure(released[0].MessageId == head.MessageId,
            $"redelivery after the extended timeout minted a different MessageId " +
            $"'{released[0].MessageId}' for what must be the same message '{head.MessageId}'");

        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = released[0].ReceiptHandle });
    }

    private static string Repr(List<Message> messages) =>
        "[" + string.Join(", ", messages.Select(m => $"{{MessageId: '{m.MessageId}', Body: '{m.Body}'}}")) + "]";
}
