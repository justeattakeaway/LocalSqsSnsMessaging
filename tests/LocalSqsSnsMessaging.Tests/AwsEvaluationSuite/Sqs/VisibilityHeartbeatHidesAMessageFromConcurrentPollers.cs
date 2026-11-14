// Translated from behavioral/sqs/check_visibility_heartbeat_hides_a_message_from_concurrent_pollers.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.visibility-heartbeat-hides-a-message-from-concurrent-pollers</summary>
[Category("AwsEvaluationSuite")]
public sealed class VisibilityHeartbeatHidesAMessageFromConcurrentPollers
{
    private const int WindowS = 10;
    private const double HeartbeatEveryS = 2.0;
    // The hold spans more than two windows, so the unheartbeated sibling lapses well inside it.
    private const double HoldS = 24.0;
    // The after-watch outlasts one full window past the last extension, so an undeleted message
    // would have reappeared within it.
    private const double AfterS = 12.0;
    private const int Pollers = 3;

    private static Dictionary<string, string> Tags() => new() { ["sim2real"] = "visibility-heartbeat-hides-a-message-from-concurrent-pollers" };

    /// <summary>
    /// Heartbeating ChangeMessageVisibility keeps a message from concurrent long-pollers while an
    /// unheartbeated sibling is handed out once its window lapses; the original receipt handle still
    /// deletes the heartbeated message afterwards and it never reappears.
    /// </summary>
    [Test]
    [DisplayName("sqs.visibility-heartbeat-hides-a-message-from-concurrent-pollers")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-heartbeat-"),
            Attributes = new() { ["VisibilityTimeout"] = WindowS.ToString(CultureInfo.InvariantCulture) },
            Tags = Tags(),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var keptBody = FidelityContext.UniqueName("kept-");
        var droppedBody = FidelityContext.UniqueName("dropped-");
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = keptBody });
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = droppedBody });

        var handles = new Dictionary<string, string>(StringComparer.Ordinal);
        var deadline = FidelityTime.StartNew();
        while (handles.Count < 2 && deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            foreach (var m in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [])
            {
                handles[m.Body] = m.ReceiptHandle;
            }
        }

        Ensure(handles.Keys.ToHashSet().SetEquals([keptBody, droppedBody]),
            $"the worker could not take both messages before heartbeating: holds [{string.Join(", ", handles.Keys.Order(StringComparer.Ordinal))}]");
        var keptHandle = handles[keptBody];

        using var stop = new CancellationTokenSource();
        var gate = new Lock();
        var polled = new List<string>();
        var heartbeatErrors = new List<string>();

        async Task Heartbeat()
        {
            var end = FidelityTime.StartNew();
            while (end.Elapsed < TimeSpan.FromSeconds(HoldS))
            {
                try
                {
                    await sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest { QueueUrl = url, ReceiptHandle = keptHandle, VisibilityTimeout = WindowS });
                }
#pragma warning disable CA1031 // asserted below
                catch (Exception exc)
#pragma warning restore CA1031
                {
                    heartbeatErrors.Add(exc.ToString());
                    return;
                }

                await FidelityTime.Delay(TimeSpan.FromSeconds(HeartbeatEveryS));
            }
        }

        async Task Poller(int _)
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var m in (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2, VisibilityTimeout = 60 })).Messages ?? [])
                {
                    lock (gate)
                    {
                        polled.Add(m.Body);
                    }

                    if (m.Body == droppedBody)
                    {
                        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
                    }
                }
            }
        }

        var pollers = Enumerable.Range(0, Pollers).Select(i => Task.Run(() => Poller(i))).ToList();
        await Task.Run(Heartbeat);
        await stop.CancelAsync();
        foreach (var f in pollers)
        {
            await f;
        }

        Ensure(heartbeatErrors.Count == 0, $"a heartbeat extension was refused: [{string.Join(", ", heartbeatErrors)}]");
        Ensure(!polled.Contains(keptBody),
            $"a concurrent poller received the heartbeated message {polled.Count(b => b == keptBody)} time(s) " +
            $"while its owner kept extending a {WindowS}s window every {HeartbeatEveryS}s");
        Ensure(polled.Count(b => b == droppedBody) >= 1,
            $"the message whose {WindowS}s window was left to lapse never reached any of " +
            $"{Pollers} pollers in {HoldS}s; pollers saw [{string.Join(", ", polled)}]");

        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = keptHandle });
        var after = new List<string>();
        var afterEnd = FidelityTime.StartNew();
        while (afterEnd.Elapsed < TimeSpan.FromSeconds(AfterS))
        {
            after.AddRange(((await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? []).Select(m => m.Body));
        }

        Ensure(!after.Contains(keptBody),
            "the original receipt handle, used after repeated extensions, did not delete the message: " +
            "it was received again");
    }
}
