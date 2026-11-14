// Translated from behavioral/sqs/check_message_attribute_names_differing_only_in_case_are_distinct.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.message-attribute-names-differing-only-in-case-are-distinct</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageAttributeNamesDifferingOnlyInCaseAreDistinct
{
    private const string Check = "message-attribute-names-differing-only-in-case-are-distinct";

    private static Dictionary<string, MessageAttributeValue> Attrs() => new()
    {
        ["Order"] = new() { DataType = "String", StringValue = "upper" },
        ["order"] = new() { DataType = "String", StringValue = "lower" },
        ["ORDER"] = new() { DataType = "String", StringValue = "allcaps" },
    };

    /// <summary>
    /// Message attribute names that differ only by case are three distinct attributes on the same
    /// message, each carrying its own sent value on receive, not one name overwriting another.
    /// </summary>
    [Test]
    [DisplayName("sqs.message-attribute-names-differing-only-in-case-are-distinct")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-attrcase-"),
            Tags = new() { ["sim2real-check"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var attrs = Attrs();
        var sent = await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "sim2real-case", MessageAttributes = Attrs() });

        var timeoutS = BudgetS(ctx, 30);
        var deadline = Stopwatch.StartNew();
        Message? received = null;
        while (deadline.Elapsed.TotalSeconds < timeoutS)
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 2, MessageAttributeNames = ["All"],
            })).Messages ?? [];
            if (msgs.Count > 0)
            {
                received = msgs[0];
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = received.ReceiptHandle });
                break;
            }
        }

        if (received is null)
        {
            throw new TimeoutException($"the message was not delivered within {timeoutS:F0}s");
        }

        Ensure(received.MessageId == sent.MessageId, "received a different message than sent");

        var got = received.MessageAttributes ?? [];
        Ensure(got.Keys.Order(StringComparer.Ordinal).SequenceEqual(attrs.Keys.Order(StringComparer.Ordinal)),
            $"expected all three case-variant names present as distinct attributes, got [{string.Join(", ", got.Keys.Order(StringComparer.Ordinal))}]");
        foreach (var (name, attr) in attrs)
        {
            var gotValue = got.GetValueOrDefault(name)?.StringValue;
            Ensure(gotValue == attr.StringValue,
                $"attribute '{name}' came back as '{gotValue}', expected '{attr.StringValue}' " +
                "(it must not have been overwritten by a different-case sibling)");
        }
    }

    /// <summary>A wait's timeout, bounded by the runner's remaining time so the wait fails with its own message.</summary>
    private static double BudgetS(FidelityContext ctx, double capS)
    {
        var left = ctx.TimeLeftSeconds;
        return Math.Max(1.0, Math.Min(capS, left - 5.0));
    }
}
