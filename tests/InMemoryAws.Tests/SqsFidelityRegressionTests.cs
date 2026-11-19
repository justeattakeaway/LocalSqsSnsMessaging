using System.Globalization;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace InMemoryAws.Tests;

/// <summary>
/// Behaviour the AWS evaluation suite does not reach: the host's culture, Number values beyond
/// <see cref="decimal"/>, concurrent FIFO sends, queue attributes changed while messages wait,
/// SNS deliveries to delayed FIFO queues, and malformed redrive allow policies.
/// </summary>
public sealed class SqsFidelityRegressionTests
{
    private static (InMemoryAwsCloud Cloud, FakeTimeProvider Time, AmazonSQSClient Sqs) Create()
    {
        var time = new FakeTimeProvider();
        var cloud = new InMemoryAwsCloud { TimeProvider = time };
        return (cloud, time, cloud.CreateSqsClient());
    }

    /// <summary>Receives until a message arrives or <paramref name="timeout"/> of real time passes, without long polling.</summary>
    private static async Task<List<Message>> PollAsync(AmazonSQSClient sqs, string url, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var messages = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, WaitTimeSeconds = 0 })).Messages ?? [];
            if (messages.Count > 0 || DateTime.UtcNow >= deadline)
            {
                return messages;
            }

            await Task.Delay(10);
        }
    }

    [Test]
    public async Task ReceiptHandle_UnderADecimalCommaCulture_StillResolvesOnceDeleted()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var (_, time, sqs) = Create();
            using var _ = sqs;
            // A receive time with a fractional second, so the handle carries a decimal separator.
            time.Advance(TimeSpan.FromMilliseconds(123));
            var url = (await sqs.CreateQueueAsync("culture-handle")).QueueUrl;
            await sqs.SendMessageAsync(url, "hello");
            var handle = (await sqs.ReceiveMessageAsync(url)).Messages.ShouldHaveSingleItem().ReceiptHandle;

            await sqs.DeleteMessageAsync(url, handle);
            // SQS accepts deleting an already-deleted message's handle.
            await sqs.DeleteMessageAsync(url, handle);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    public async Task NumberAttribute_InExponentNotationBeyondDecimal_IsWrittenOutExactly()
    {
        var (_, _, sqs) = Create();
        using var _ = sqs;
        var url = (await sqs.CreateQueueAsync("number-plain")).QueueUrl;
        const string ThirtyTwoDigits = "1.2345678901234567890123456789012e3";

        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = "numbers",
            MessageAttributes = new()
            {
                ["Tiny"] = new() { DataType = "Number", StringValue = "1e-30" },
                ["Precise"] = new() { DataType = "Number", StringValue = ThirtyTwoDigits },
                ["Large"] = new() { DataType = "Number", StringValue = "-2.5e29" },
            },
        });

        var attributes = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MessageAttributeNames = ["All"] }))
            .Messages.ShouldHaveSingleItem().MessageAttributes;
        attributes["Tiny"].StringValue.ShouldBe("0." + new string('0', 29) + "1");
        attributes["Precise"].StringValue.ShouldBe("1234.5678901234567890123456789012");
        attributes["Large"].StringValue.ShouldBe("-25" + new string('0', 28));
    }

    [Test]
    public async Task FifoSends_ConcurrentToOneGroup_AreDeliveredInSequenceNumberOrder()
    {
        var (_, _, sqs) = Create();
        using var _ = sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "concurrent-sends.fifo",
            Attributes = new() { ["FifoQueue"] = "true" },
        })).QueueUrl;
        const int Senders = 8;
        const int PerSender = 50;

        await Task.WhenAll(Enumerable.Range(0, Senders).Select(sender => Task.Run(async () =>
        {
            for (var i = 0; i < PerSender; i++)
            {
                await sqs.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = url,
                    MessageBody = $"{sender}-{i}",
                    MessageGroupId = "g",
                    MessageDeduplicationId = $"{sender}-{i}",
                });
            }
        })));

        var sequenceNumbers = new List<decimal>();
        while (sequenceNumbers.Count < Senders * PerSender)
        {
            var messages = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 10,
                MessageSystemAttributeNames = ["SequenceNumber"],
            })).Messages.ShouldNotBeNull();
            messages.ShouldNotBeEmpty();
            foreach (var message in messages)
            {
                sequenceNumbers.Add(decimal.Parse(message.Attributes["SequenceNumber"], CultureInfo.InvariantCulture));
                await sqs.DeleteMessageAsync(url, message.ReceiptHandle);
            }
        }

        sequenceNumbers.ShouldBe(sequenceNumbers.Order().ToList());
    }

    [Test]
    public async Task FifoQueue_DelayLoweredWhileAMessageWaits_ReleasesItAtTheNewDelay()
    {
        var (_, time, sqs) = Create();
        using var _ = sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "lowered-delay.fifo",
            Attributes = new() { ["FifoQueue"] = "true", ["DelaySeconds"] = "10" },
        })).QueueUrl;
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "a", MessageGroupId = "g", MessageDeduplicationId = "a" });

        time.Advance(TimeSpan.FromSeconds(2));
        await sqs.SetQueueAttributesAsync(url, new() { ["DelaySeconds"] = "5" });
        time.Advance(TimeSpan.FromSeconds(2));
        (await PollAsync(sqs, url, TimeSpan.FromMilliseconds(100))).ShouldBeEmpty();

        // t=6: past the new 5-second delay, well short of the original 10.
        time.Advance(TimeSpan.FromSeconds(2));
        (await PollAsync(sqs, url, TimeSpan.FromSeconds(5))).ShouldHaveSingleItem().Body.ShouldBe("a");
    }

    [Test]
    public async Task SnsDelivery_ToAFifoQueueWithADelay_WaitsOutTheDelay()
    {
        var (cloud, time, sqs) = Create();
        using var _ = sqs;
        using var sns = cloud.CreateSnsClient();
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "sns-delayed.fifo",
            Attributes = new() { ["FifoQueue"] = "true", ["DelaySeconds"] = "30" },
        })).QueueUrl;
        var queueArn = (await sqs.GetQueueAttributesAsync(url, ["QueueArn"])).Attributes["QueueArn"];
        var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = "sns-delayed.fifo",
            Attributes = new() { ["FifoTopic"] = "true" },
        })).TopicArn;
        await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new() { ["RawMessageDelivery"] = "true" },
        });

        await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = "delayed", MessageGroupId = "g", MessageDeduplicationId = "d" });
        (await PollAsync(sqs, url, TimeSpan.FromMilliseconds(100))).ShouldBeEmpty();

        time.Advance(TimeSpan.FromSeconds(30));
        (await PollAsync(sqs, url, TimeSpan.FromSeconds(5))).ShouldHaveSingleItem().Body.ShouldBe("delayed");
    }

    [Test]
    public async Task ZeroVisibilityReceive_WhenTheQueueTimeoutChangesDuringTheLongPoll_StillEndsVisibility()
    {
        var (cloud, _, sqs) = Create();
        using var _ = sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "zero-visibility-change.fifo",
            Attributes = new() { ["FifoQueue"] = "true", ["VisibilityTimeout"] = "0" },
        })).QueueUrl;

        var longPoll = sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, WaitTimeSeconds = 20 });
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (cloud.WaitingReceiveCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        cloud.WaitingReceiveCount.ShouldBe(1);
        await sqs.SetQueueAttributesAsync(url, new() { ["VisibilityTimeout"] = "30" });
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "m", MessageGroupId = "g", MessageDeduplicationId = "m" });
        (await longPoll).Messages.ShouldHaveSingleItem();

        // Received under a zero timeout, so it is visible again at once.
        (await PollAsync(sqs, url, TimeSpan.FromSeconds(5))).ShouldHaveSingleItem().Body.ShouldBe("m");
    }

    [Test]
    public async Task RedriveAllowPolicy_WithANonStringSourceQueueArn_IsRefused()
    {
        var (_, _, sqs) = Create();
        using var _ = sqs;

        var exception = await Should.ThrowAsync<AmazonSQSException>(() => sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "allow-policy-number",
            Attributes = new() { ["RedriveAllowPolicy"] = """{"redrivePermission":"byQueue","sourceQueueArns":[123]}""" },
        }));

        exception.ErrorCode.ShouldBe("InvalidAttributeValue");
    }

    [Test]
    public async Task FifoThroughputLimitPerMessageGroup_WithoutAScope_IsRefusedOnCreate()
    {
        var (_, _, sqs) = Create();
        using var _ = sqs;

        // The scope defaults to queue, a pairing SQS refuses; accepting it would leave a queue whose
        // every later SetQueueAttributes fails the same check.
        var exception = await Should.ThrowAsync<AmazonSQSException>(() => sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "throughput-no-scope.fifo",
            Attributes = new() { ["FifoQueue"] = "true", ["FifoThroughputLimit"] = "perMessageGroupId" },
        }));

        exception.ErrorCode.ShouldBe("InvalidAttributeValue");
    }

    [Test]
    [Arguments("Policy")]
    [Arguments("RedrivePolicy")]
    public async Task CreateQueue_RepeatedWithAnEmptyOptionalPolicy_ReturnsTheExistingQueue(string attribute)
    {
        var (_, _, sqs) = Create();
        using var _ = sqs;
        var request = new CreateQueueRequest { QueueName = "empty-" + attribute, Attributes = new() { [attribute] = "" } };

        var first = (await sqs.CreateQueueAsync(request)).QueueUrl;
        var second = (await sqs.CreateQueueAsync(request)).QueueUrl;

        second.ShouldBe(first);
    }

    [Test]
    public async Task ChangeMessageVisibility_OnAMessageThatExpiredInFlight_IsRefused()
    {
        var (_, time, sqs) = Create();
        using var _ = sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "expired-in-flight",
            Attributes = new() { ["MessageRetentionPeriod"] = "60", ["VisibilityTimeout"] = "120" },
        })).QueueUrl;
        await sqs.SendMessageAsync(url, "short-lived");
        var handle = (await sqs.ReceiveMessageAsync(url)).Messages.ShouldHaveSingleItem().ReceiptHandle;

        // The visibility timeout ends after retention has, so the message is discarded, not returned.
        time.Advance(TimeSpan.FromSeconds(121));

        var exception = await Should.ThrowAsync<AmazonSQSException>(() => sqs.ChangeMessageVisibilityAsync(url, handle, 30));
        exception.ErrorCode.ShouldBe("InvalidParameterValue");
    }

    [Test]
    public async Task ChangeMessageVisibility_OnAMessageThatExpiredWhileVisible_IsRefused()
    {
        var (_, time, sqs) = Create();
        using var _ = sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "expired-visible",
            Attributes = new() { ["MessageRetentionPeriod"] = "60", ["VisibilityTimeout"] = "10" },
        })).QueueUrl;
        await sqs.SendMessageAsync(url, "short-lived");
        var handle = (await sqs.ReceiveMessageAsync(url)).Messages.ShouldHaveSingleItem().ReceiptHandle;

        // Visible again after 10s, then past retention at 60s.
        time.Advance(TimeSpan.FromSeconds(61));

        var exception = await Should.ThrowAsync<AmazonSQSException>(() => sqs.ChangeMessageVisibilityAsync(url, handle, 30));
        exception.ErrorCode.ShouldBe("InvalidParameterValue");
        (await sqs.GetQueueAttributesAsync(url, ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"]))
            .Attributes.ShouldBe(new Dictionary<string, string>
            {
                ["ApproximateNumberOfMessages"] = "0",
                ["ApproximateNumberOfMessagesNotVisible"] = "0",
            });
    }

    [Test]
    public async Task MessageMoveTask_FromAFifoDeadLetterQueue_RedrivesItsMessagesInOrder()
    {
        var (_, time, sqs) = Create();
        using var _ = sqs;
        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "redrive-dlq.fifo",
            Attributes = new() { ["FifoQueue"] = "true" },
        })).QueueUrl;
        var dlqArn = (await sqs.GetQueueAttributesAsync(dlqUrl, ["QueueArn"])).QueueARN;
        var sourceUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "redrive-source.fifo",
            Attributes = new()
            {
                ["FifoQueue"] = "true",
                ["VisibilityTimeout"] = "5",
                ["RedrivePolicy"] = $$"""{"deadLetterTargetArn":"{{dlqArn}}","maxReceiveCount":1}""",
            },
        })).QueueUrl;
        foreach (var body in new[] { "a1", "a2", "b1" })
        {
            await sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = sourceUrl, MessageBody = body, MessageGroupId = body[..1], MessageDeduplicationId = body,
            });
        }

        // Receive everything once, let it lapse, and the next receive dead-letters it all.
        (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = sourceUrl, MaxNumberOfMessages = 10 })).Messages.Count.ShouldBe(3);
        time.Advance(TimeSpan.FromSeconds(6));
        ((await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = sourceUrl, MaxNumberOfMessages = 10 })).Messages ?? []).ShouldBeEmpty();
        (await sqs.GetQueueAttributesAsync(dlqUrl, ["ApproximateNumberOfMessages"])).ApproximateNumberOfMessages.ShouldBe(3);

        await sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest { SourceArn = dlqArn });
        time.Advance(TimeSpan.FromSeconds(2));

        var task = (await sqs.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest { SourceArn = dlqArn })).Results.ShouldHaveSingleItem();
        task.Status.ShouldBe("COMPLETED");
        task.ApproximateNumberOfMessagesMoved.ShouldBe(3);
        task.ApproximateNumberOfMessagesToMove.ShouldBe(3);
        (await sqs.GetQueueAttributesAsync(dlqUrl, ["ApproximateNumberOfMessages"])).ApproximateNumberOfMessages.ShouldBe(0);

        var redriven = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = sourceUrl, MaxNumberOfMessages = 10 })).Messages;
        redriven.Where(m => m.Body.StartsWith('a')).Select(m => m.Body).ShouldBe(["a1", "a2"]);
        redriven.Select(m => m.Body).ShouldBe(["a1", "a2", "b1"], ignoreOrder: true);
    }
}
