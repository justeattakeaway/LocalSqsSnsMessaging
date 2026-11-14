// Translated from behavioral/sqs/check_numeric_message_attribute_precision_preserved.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.numeric-message-attribute-precision-preserved</summary>
[Category("AwsEvaluationSuite")]
public sealed class NumericMessageAttributePrecisionPreserved
{
    private static Dictionary<string, string> Tags => new()
    {
        ["sim2real:managed"] = "true",
        ["sim2real"] = "numeric-message-attribute-precision-preserved",
    };

    /// <summary>
    /// A Number-typed message attribute is transmitted and stored as its literal string
    /// value (SQS requires StringValue for Number attributes), so a high-precision decimal
    /// round-trips character-for-character with no floating-point truncation or rounding.
    /// </summary>
    [Test]
    [DisplayName("sqs.numeric-message-attribute-precision-preserved")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-num-"), Tags = Tags })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        const string preciseValue = "3.14159265358979323846"; // 20 significant fractional digits
        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = "precision-test",
            MessageAttributes = new() { ["Precise"] = new() { DataType = "Number", StringValue = preciseValue } },
        });

        var received = await PollForMessage(ctx, url);
        var attr = received.MessageAttributes!["Precise"];
        Ensure(attr.DataType == "Number", $"expected DataType Number, got '{attr.DataType}'");
        Ensure(attr.StringValue == preciseValue, $"precision lost: '{attr.StringValue}' != '{preciseValue}'");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = received.ReceiptHandle });
    }

    private static async Task<Message> PollForMessage(FidelityContext ctx, string url, double timeoutSeconds = 30.0)
    {
        var sqs = ctx.Sqs;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"],
            });
            if (resp.Messages is { Count: > 0 } msgs)
            {
                return msgs[0];
            }
        }

        throw Fail("no message received within timeout");
    }
}
