using System.Text;
using System.Text.Json;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Shouldly;

namespace LocalSqsSnsMessaging.Tests.Sns;

/// <summary>
/// Publishing under concurrency and hostile input: no deadlock between delivery and topic changes,
/// no stack overflow on a deeply nested filter policy, and one signature per message.
/// </summary>
public sealed class SnsPublishRobustnessTests : IDisposable
{
    private readonly InMemoryAwsBus _bus = new();
    private readonly AmazonSimpleNotificationServiceClient _sns;
    private readonly AmazonSQSClient _sqs;

    public SnsPublishRobustnessTests()
    {
        _sns = _bus.CreateSnsClient();
        _sqs = _bus.CreateSqsClient();
    }

    public void Dispose()
    {
        _sns.Dispose();
        _sqs.Dispose();
    }

    /// <summary>
    /// A FIFO publish delivers under the topic's FIFO gate, and writing an envelope reads topic
    /// attributes; Subscribe and SetTopicAttributes take the topic's subscription gate and then the
    /// FIFO gate. Delivery must not take the subscription gate, or the two orders deadlock.
    /// </summary>
    [Test]
    [Timeout(60_000)]
    public async Task FifoPublishesRunAlongsideSubscribeAndSetTopicAttributes(CancellationToken cancellationToken)
    {
        var topicArn = (await _sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = "concurrent.fifo",
            Attributes = new() { ["FifoTopic"] = "true", ["ContentBasedDeduplication"] = "true" },
        }, cancellationToken)).TopicArn;
        var queueArn = await CreateQueueArn("concurrent-0.fifo", fifo: true);
        await _sns.SubscribeAsync(topicArn, "sqs", queueArn, cancellationToken);

        var publishing = Task.Run(async () =>
        {
            for (var i = 0; i < 300; i++)
            {
                await _sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = $"m{i}", MessageGroupId = "g" }, cancellationToken);
            }
        }, cancellationToken);
        var changing = Task.Run(async () =>
        {
            for (var i = 1; i <= 30; i++)
            {
                await _sns.SetTopicAttributesAsync(topicArn, "DisplayName", $"name-{i}", cancellationToken);
                await _sns.SubscribeAsync(topicArn, "sqs", await CreateQueueArn($"concurrent-{i}.fifo", fifo: true), cancellationToken);
            }
        }, cancellationToken);

        await Task.WhenAll(publishing, changing).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
    }

    [Test]
    public async Task ADeeplyNestedFilterPolicyIsRefusedNotOverflowed()
    {
        var topicArn = (await _sns.CreateTopicAsync("nested")).TopicArn;
        var queueArn = await CreateQueueArn("nested-queue", fifo: false);
        const int depth = 100_000;
        var policy = new StringBuilder("{\"a\":").Append('[', depth).Append(']', depth).Append('}').ToString();

        var refused = await Should.ThrowAsync<InvalidParameterException>(() => _sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new() { ["FilterPolicy"] = policy },
        }));
        refused.Message.ShouldContain("nesting depth");
    }

    /// <summary>
    /// The UnsubscribeURL is the only envelope member that differs between subscriptions, and it isn't
    /// signed, so every subscription gets the same Timestamp and Signature for one message.
    /// </summary>
    [Test]
    public async Task EverySubscriptionGetsTheMessagesOneTimestampAndSignature()
    {
        var topicArn = (await _sns.CreateTopicAsync("signed-once")).TopicArn;
        var queueUrls = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var url = (await _sqs.CreateQueueAsync($"signed-once-{i}")).QueueUrl;
            queueUrls.Add(url);
            await _sns.SubscribeAsync(topicArn, "sqs", (await _sqs.GetQueueAttributesAsync(url, ["QueueArn"])).QueueARN);
        }

        await _sns.PublishAsync(topicArn, "once");

        var envelopes = new List<JsonElement>();
        foreach (var url in queueUrls)
        {
            var body = (await _sqs.ReceiveMessageAsync(url)).Messages.ShouldHaveSingleItem().Body;
            envelopes.Add(JsonDocument.Parse(body).RootElement.Clone());
        }

        envelopes.Select(e => e.GetProperty("Timestamp").GetString()).Distinct().ShouldHaveSingleItem();
        envelopes.Select(e => e.GetProperty("Signature").GetString()).Distinct().ShouldHaveSingleItem();
        envelopes.Select(e => e.GetProperty("UnsubscribeURL").GetString()).Distinct().Count().ShouldBe(3);
    }

    private async Task<string> CreateQueueArn(string name, bool fifo)
    {
        var url = (await _sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = fifo ? new() { ["FifoQueue"] = "true" } : new(),
        })).QueueUrl;
        return (await _sqs.GetQueueAttributesAsync(url, ["QueueArn"])).QueueARN;
    }
}
