using InMemoryAws.Sqs.Model;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace InMemoryAws.Tests;

/// <summary>
/// The message lifecycle at the queue's own interface (<see cref="SqsQueueResource"/>): however a
/// message leaves flight, its FIFO message group delivers again, and its receipt handle stops
/// acting on it once it has left the queue.
/// </summary>
public sealed class SqsQueueLifecycleTests
{
    public enum FlightExit
    {
        Deleted,
        VisibilitySetToZero,
        VisibilityTimeoutEnded,
        RetentionPeriodEnded,
        Purged,
        RemovedById,
    }

    // Ends 20 seconds after the receive, before "first" outlives the 60-second retention period.
    private static readonly TimeSpan VisibilityTimeout = TimeSpan.FromSeconds(20);

    [Test]
    [MethodDataSource(nameof(Exits))]
    public async Task LeavingFlight_ReleasesTheMessageGroup_AndRetiresTheReceiptHandleOfAMessageThatLeftTheQueue(FlightExit exit)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var queue = await CreateFifoQueueAsync(time, retentionSeconds: 60);
        Send(queue, time, "first");
        time.Advance(TimeSpan.FromSeconds(30));
        Send(queue, time, "second");

        var received = (await ReceiveAsync(queue, maxMessages: 1)).ShouldHaveSingleItem();
        received.Body.ShouldBe("first");
        (await ReceiveAsync(queue)).ShouldBeEmpty(); // the group is locked while "first" is in flight

        switch (exit)
        {
            case FlightExit.Deleted:
                queue.DeleteMessage(received.ReceiptHandle).ShouldBe(SqsReceiptHandleOutcome.Done);
                break;
            case FlightExit.VisibilitySetToZero:
                queue.ChangeVisibility(received.ReceiptHandle, TimeSpan.Zero).ShouldBe(SqsReceiptHandleOutcome.Done);
                break;
            case FlightExit.VisibilityTimeoutEnded:
                time.Advance(VisibilityTimeout);
                break;
            case FlightExit.RetentionPeriodEnded:
                // "first" has been kept 60 seconds, "second" only 30.
                time.Advance(TimeSpan.FromSeconds(30));
                break;
            case FlightExit.Purged:
                queue.Purge();
                break;
            case FlightExit.RemovedById:
                queue.RemoveMessage(received.MessageId!).ShouldBeTrue();
                break;
        }

        queue.CountMessages().InFlight.ShouldBe(0);
        Send(queue, time, "sent after");

        var leftTheQueue = exit is not (FlightExit.VisibilitySetToZero or FlightExit.VisibilityTimeoutEnded);
        var expectedNext = exit switch
        {
            FlightExit.Purged => "sent after",
            _ when leftTheQueue => "second",
            _ => "first",
        };
        (await ReceiveAsync(queue, maxMessages: 1)).ShouldHaveSingleItem().Body.ShouldBe(expectedNext);

        // A message back in the queue was just received again, so the old handle is stale; one that
        // left the queue is no longer there to act on.
        queue.ChangeVisibility(received.ReceiptHandle, TimeSpan.FromSeconds(10)).ShouldBe(leftTheQueue
            ? SqsReceiptHandleOutcome.MessageNotAvailable
            : SqsReceiptHandleOutcome.Expired);
    }

    public static IEnumerable<FlightExit> Exits() => Enum.GetValues<FlightExit>();

    [Test]
    public async Task FifoReceive_WhenReadingTheClockFiresAVisibilityTimer_HandsEachMessageOutOnce()
    {
        // A clock that moves on every read runs due timers from inside the read, so a message's
        // visibility can end while the same receive is still taking messages from its group.
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow) { AutoAdvanceAmount = TimeSpan.FromSeconds(1) };
        var queue = await CreateFifoQueueAsync(time, retentionSeconds: 3600);
        foreach (var body in new[] { "a", "b", "c", "d" })
        {
            Send(queue, time, body);
        }

        var batch = await queue.ReceiveAsync(new SqsQueueResource.ReceiveRequest(10, TimeSpan.FromSeconds(1), WaitTime: TimeSpan.Zero, AttemptId: null),
            CancellationToken.None);

        batch.Select(m => m.Body).ShouldBe(["a", "b", "c", "d"]);
    }

    private static async Task<SqsQueueResource> CreateFifoQueueAsync(TimeProvider time, int retentionSeconds)
    {
        var cloud = new InMemoryAwsCloud { TimeProvider = time };
        await new InternalSqsClient(cloud).CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = "lifecycle.fifo",
            Attributes = new()
            {
                ["FifoQueue"] = "true",
                ["MessageRetentionPeriod"] = retentionSeconds.ToString(CultureInfo.InvariantCulture),
            }
        });
        return cloud.Queues["lifecycle.fifo"];
    }

    private static void Send(SqsQueueResource queue, TimeProvider time, string body) =>
        queue.AcceptSent(new Message { MessageId = Guid.NewGuid().ToString(), Body = body },
            messageGroupId: "g", deduplicationId: body, delaySeconds: null, time, out _).ShouldBeTrue();

    private static Task<List<Message>> ReceiveAsync(SqsQueueResource queue, int maxMessages = 10) =>
        queue.ReceiveAsync(new SqsQueueResource.ReceiveRequest(maxMessages, VisibilityTimeout, WaitTime: TimeSpan.Zero, AttemptId: null),
            CancellationToken.None);
}
