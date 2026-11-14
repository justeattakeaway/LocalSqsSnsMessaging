// Translated from behavioral/sqs/check_receive_count_increments.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.receive-count-increments</summary>
[Category("AwsEvaluationSuite")]
public sealed class ReceiveCountIncrements
{
    /// <summary>
    /// ApproximateReceiveCount increments across receives (system attribute on receive).
    /// </summary>
    [Test]
    [DisplayName("sqs.receive-count-increments")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.LegacyReceiveAttributeNames, "expected ApproximateReceiveCount=1, got None")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "receive-count-increments" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "recv-count" });
        var first = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 2,
            AttributeNames = ["All"],
        })).Messages ?? [];
        if (first.Count == 0)
        {
            throw Fail("no message received");
        }

        var attrs = first[0].Attributes ?? [];
        if (attrs.GetValueOrDefault("ApproximateReceiveCount") != "1")
        {
            throw Fail($"expected ApproximateReceiveCount=1, got {attrs.GetValueOrDefault("ApproximateReceiveCount") ?? "None"}");
        }

        // Re-expose and receive again — count must advance to 2.
        await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = url,
            ReceiptHandle = first[0].ReceiptHandle,
            VisibilityTimeout = 0,
        });
        List<Message> second = [];
        HashSet<string> seen = [];
        var idle = 0;
        for (var _ = 0; _ < 12; _++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 1,
                AttributeNames = ["All"],
            });
            var msgs = resp.Messages ?? [];
            if (msgs.Count == 0)
            {
                idle++;
                if (second.Count > 0 && idle >= 3)
                {
                    break;
                }

                continue;
            }

            idle = 0;
            foreach (var m in msgs)
            {
                if (seen.Add(m.MessageId))
                {
                    second.Add(m);
                }
            }

            if (second.Count >= 1)
            {
                break;
            }
        }

        if (second.Count == 0)
        {
            throw Fail("message not re-received");
        }

        var count = (second[0].Attributes ?? []).GetValueOrDefault("ApproximateReceiveCount");
        if (count != "2")
        {
            throw Fail($"expected ApproximateReceiveCount=2 on second receive, got {count ?? "None"}");
        }
    }
}
