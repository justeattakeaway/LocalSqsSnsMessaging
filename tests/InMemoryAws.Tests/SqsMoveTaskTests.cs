using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace InMemoryAws.Tests;

/// <summary>
/// Message move tasks beyond what the AWS evaluation suite reaches: FIFO dead-letter queues, a
/// destination deleted and recreated while the task runs, and a cancel that races a move.
/// </summary>
public sealed class SqsMoveTaskTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly InMemoryAwsCloud _cloud;
    private readonly AmazonSQSClient _sqs;

    public SqsMoveTaskTests()
    {
        _cloud = new InMemoryAwsCloud { TimeProvider = _time };
        _sqs = _cloud.CreateSqsClient();
    }

    public void Dispose() => _sqs.Dispose();

    [Test]
    public async Task MoveTask_FromAFifoDeadLetterQueue_MovesItsMessagesBackToTheSourceInOrder()
    {
        var dlqUrl = await CreateQueueAsync("move-dlq.fifo", new() { ["FifoQueue"] = "true" });
        var dlqArn = await ArnOfAsync(dlqUrl);
        var sourceUrl = await CreateQueueAsync("move-source.fifo", new()
        {
            ["FifoQueue"] = "true",
            ["ContentBasedDeduplication"] = "true",
            ["RedrivePolicy"] = RedrivePolicy(dlqArn),
        });
        await _sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = sourceUrl, MessageBody = "first", MessageGroupId = "g" });
        await _sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = sourceUrl, MessageBody = "second", MessageGroupId = "g" });

        // Received once with a zero visibility timeout, so both are visible again at once; the next
        // receive finds them at maxReceiveCount and dead-letters them.
        (await ReceiveAsync(sourceUrl, visibilityTimeout: 0)).Count.ShouldBe(2);
        (await ReceiveAsync(sourceUrl)).ShouldBeEmpty();
        (await VisibleCountAsync(dlqUrl)).ShouldBe(2);

        await _sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest { SourceArn = dlqArn });
        var running = await LatestTaskAsync(dlqArn);
        running.ApproximateNumberOfMessagesToMove.ShouldBe(2);

        _time.Advance(TimeSpan.FromSeconds(1));

        var task = await LatestTaskAsync(dlqArn);
        task.Status.ShouldBe("COMPLETED");
        task.ApproximateNumberOfMessagesMoved.ShouldBe(2);
        (await VisibleCountAsync(dlqUrl)).ShouldBe(0);
        (await ReceiveAsync(sourceUrl)).Select(m => m.Body).ShouldBe(["first", "second"]);
    }

    [Test]
    public async Task MoveTask_ToADestinationDeletedAndRecreated_DeliversToTheRecreatedQueue()
    {
        var dlqUrl = await CreateQueueAsync("recreated-dlq");
        var dlqArn = await ArnOfAsync(dlqUrl);
        await CreateQueueAsync("recreated-source", new() { ["RedrivePolicy"] = RedrivePolicy(dlqArn) });
        var destinationUrl = await CreateQueueAsync("recreated-destination");
        var destinationArn = await ArnOfAsync(destinationUrl);
        await _sqs.SendMessageAsync(dlqUrl, "stranded");

        await _sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest { SourceArn = dlqArn, DestinationArn = destinationArn });

        // The same ARN names the same target, whichever queue now holds it.
        await _sqs.DeleteQueueAsync(destinationUrl);
        var recreatedUrl = await CreateQueueAsync("recreated-destination");
        _time.Advance(TimeSpan.FromSeconds(1));

        (await LatestTaskAsync(dlqArn)).Status.ShouldBe("COMPLETED");
        (await ReceiveAsync(recreatedUrl)).ShouldHaveSingleItem().Body.ShouldBe("stranded");
    }

    [Test]
    public async Task MoveTask_ToADestinationDeletedAndNotRecreated_FailsAndLeavesTheMessages()
    {
        var dlqUrl = await CreateQueueAsync("orphaned-dlq");
        var dlqArn = await ArnOfAsync(dlqUrl);
        await CreateQueueAsync("orphaned-source", new() { ["RedrivePolicy"] = RedrivePolicy(dlqArn) });
        var destinationUrl = await CreateQueueAsync("orphaned-destination");
        await _sqs.SendMessageAsync(dlqUrl, "kept");
        await _sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest { SourceArn = dlqArn, DestinationArn = await ArnOfAsync(destinationUrl) });

        await _sqs.DeleteQueueAsync(destinationUrl);
        _time.Advance(TimeSpan.FromSeconds(1));

        // Settled, so the dead-letter queue can be given a new task.
        (await LatestTaskAsync(dlqArn)).Status.ShouldBe("FAILED");
        (await VisibleCountAsync(dlqUrl)).ShouldBe(1);
    }

    [Test]
    [Repeat(5)]
    public async Task MoveTask_CancelledWhileItIsMovingMessages_EndsCancelledExactlyWhenTheCancelIsAccepted()
    {
        const int Count = 2000;
        var dlqUrl = await CreateQueueAsync("race-dlq");
        var dlqArn = await ArnOfAsync(dlqUrl);
        await CreateQueueAsync("race-source", new() { ["RedrivePolicy"] = RedrivePolicy(dlqArn) });
        var destinationUrl = await CreateQueueAsync("race-destination");
        var destinationArn = await ArnOfAsync(destinationUrl);
        for (var batch = 0; batch < Count / 10; batch++)
        {
            await _sqs.SendMessageBatchAsync(dlqUrl, [.. Enumerable.Range(0, 10).Select(i =>
                new SendMessageBatchRequestEntry(i.ToString(CultureInfo.InvariantCulture), $"m{batch}-{i}"))]);
        }

        var handle = (await _sqs.StartMessageMoveTaskAsync(new StartMessageMoveTaskRequest
        {
            SourceArn = dlqArn,
            DestinationArn = destinationArn,
        })).TaskHandle;

        // One tick moves every message. Cancel once it has visibly started moving them.
        var tick = Task.Run(() => _time.Advance(TimeSpan.FromSeconds(1)));
        while (!tick.IsCompleted && await VisibleCountAsync(dlqUrl) == Count)
        {
        }

        bool accepted;
        try
        {
            await _sqs.CancelMessageMoveTaskAsync(new CancelMessageMoveTaskRequest { TaskHandle = handle });
            accepted = true;
        }
        catch (ResourceNotFoundException)
        {
            accepted = false;
        }

        await tick;
        (await LatestTaskAsync(dlqArn)).Status.ShouldBe(accepted ? "CANCELLED" : "COMPLETED");
    }

    private static string RedrivePolicy(string deadLetterArn) =>
        $$"""{"deadLetterTargetArn":"{{deadLetterArn}}","maxReceiveCount":1}""";

    private async Task<string> CreateQueueAsync(string name, Dictionary<string, string>? attributes = null) =>
        (await _sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = attributes })).QueueUrl;

    private async Task<string> ArnOfAsync(string url) =>
        (await _sqs.GetQueueAttributesAsync(url, ["QueueArn"])).Attributes["QueueArn"];

    private async Task<int> VisibleCountAsync(string url) =>
        int.Parse((await _sqs.GetQueueAttributesAsync(url, ["ApproximateNumberOfMessages"])).Attributes["ApproximateNumberOfMessages"],
            CultureInfo.InvariantCulture);

    private async Task<List<Message>> ReceiveAsync(string url, int? visibilityTimeout = null) =>
        (await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 10,
            VisibilityTimeout = visibilityTimeout,
        })).Messages ?? [];

    private async Task<ListMessageMoveTasksResultEntry> LatestTaskAsync(string sourceArn) =>
        (await _sqs.ListMessageMoveTasksAsync(new ListMessageMoveTasksRequest { SourceArn = sourceArn })).Results.ShouldHaveSingleItem();
}
