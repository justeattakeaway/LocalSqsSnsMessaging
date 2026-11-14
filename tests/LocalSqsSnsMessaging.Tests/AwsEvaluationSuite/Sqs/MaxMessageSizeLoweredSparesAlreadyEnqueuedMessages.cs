// Translated from behavioral/sqs/check_max_message_size_lowered_spares_already_enqueued_messages.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Globalization;
using Amazon.Runtime;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.max-message-size-lowered-spares-already-enqueued-messages</summary>
[Category("AwsEvaluationSuite")]
public sealed class MaxMessageSizeLoweredSparesAlreadyEnqueuedMessages
{
    private static readonly Dictionary<string, string> Tags = new()
    {
        ["sim2real:managed"] = "true",
        ["sim2real"] = "max-size-lowered",
    };

    private const int NewLimit = 1024;
    private const int Big = 4096;
    // Bound on how long the lowered limit may take to govern SendMessage.
    private const double ConvergeS = 90.0;

    /// <summary>
    /// Lowering MaximumMessageSize only bounds future sends.
    ///
    /// Oversize sends are then refused, but a larger message enqueued before is delivered byte-exact.
    /// </summary>
    [Test]
    [DisplayName("sqs.max-message-size-lowered-spares-already-enqueued-messages")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Other, "Check timed out after 60.0s (a 4096-byte body is still accepted after lowering MaximumMessageSize to 1024)")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var token = Guid.NewGuid().ToString("N")[..8];
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-"), Tags = new(Tags) })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var head = $"before-change-{token}-";
        var bigBody = head + new string('x', Big - head.Length);
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = bigBody });

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url, Attributes = new() { ["MaximumMessageSize"] = NewLimit.ToString(CultureInfo.InvariantCulture) },
        });
        var stored = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["MaximumMessageSize"] })).Attributes!["MaximumMessageSize"];
        Ensure(stored == NewLimit.ToString(CultureInfo.InvariantCulture), $"MaximumMessageSize read back '{stored}'");

        // Wait until the new limit refuses a body the size of the stored one.
        List<string> acceptedLate = [];
        (string Code, string? Message)? refused = null;
        var deadline = Stopwatch.StartNew();
        while (refused is null && deadline.Elapsed.TotalSeconds < ConvergeS)
        {
            var probeHead = $"after-change-{acceptedLate.Count}-{token}-";
            var probe = probeHead + new string('y', Big - probeHead.Length);
            try
            {
                await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = probe });
                acceptedLate.Add(probe);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            catch (AmazonServiceException exc)
            {
                refused = (exc.ErrorCode ?? throw new KeyNotFoundException("Code"), exc.Message);
            }
        }

        Ensure(refused is not null,
            $"a {Big}-byte body was still accepted {ConvergeS:F0}s after lowering " +
            $"MaximumMessageSize to {NewLimit}");
        Ensure(refused == ("InvalidParameterValue",
                $"One or more parameters are invalid. Reason: Message must be shorter " +
                $"than {NewLimit} bytes."), $"oversize send refused with {refused}");

        var atLimitHead = $"at-limit-{token}-";
        var atLimit = atLimitHead + new string('z', NewLimit - atLimitHead.Length);
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = atLimit });

        List<string> bodies = [];
        var empties = 0;
        deadline = Stopwatch.StartNew();
        while (empties < 3 && deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            var batch = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
            empties = batch.Count > 0 ? 0 : empties + 1;
            foreach (var m in batch)
            {
                bodies.Add(m.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        var sizes = string.Join(", ", bodies.Select(b => b.Length).Order());
        Ensure(bodies.Contains(bigBody),
            $"the {Big}-byte message enqueued before MaximumMessageSize was lowered to {NewLimit} " +
            $"was not delivered intact; received sizes [{sizes}]");
        Ensure(bodies.Contains(atLimit), $"the {NewLimit}-byte body sent after the change was not delivered");
        Ensure(bodies.Order(StringComparer.Ordinal).SequenceEqual(new[] { bigBody, atLimit }.Concat(acceptedLate).Order(StringComparer.Ordinal)),
            "queue must hold exactly the pre-change message, the sends accepted while the limit " +
            $"propagated, and the at-limit body; received sizes [{sizes}]");
    }
}
