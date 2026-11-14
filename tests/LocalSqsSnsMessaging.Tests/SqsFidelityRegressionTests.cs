using System.Globalization;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests;

/// <summary>
/// Behaviour the AWS evaluation suite does not reach: the host's culture, Number values beyond
/// <see cref="decimal"/>, concurrent FIFO sends, queue attributes changed while messages wait,
/// SNS deliveries to delayed FIFO queues, and malformed redrive allow policies.
/// </summary>
public sealed class SqsFidelityRegressionTests
{
    private static (InMemoryAwsBus Bus, FakeTimeProvider Time, AmazonSQSClient Sqs) Create()
    {
        var time = new FakeTimeProvider();
        var bus = new InMemoryAwsBus { TimeProvider = time };
        return (bus, time, bus.CreateSqsClient());
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
        var (bus, time, sqs) = Create();
        using var _ = sqs;
        using var sns = bus.CreateSnsClient();
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
        var (bus, _, sqs) = Create();
        using var _ = sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "zero-visibility-change.fifo",
            Attributes = new() { ["FifoQueue"] = "true", ["VisibilityTimeout"] = "0" },
        })).QueueUrl;

        var longPoll = sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, WaitTimeSeconds = 20 });
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (bus.WaitingReceiveCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        bus.WaitingReceiveCount.ShouldBe(1);
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
}
