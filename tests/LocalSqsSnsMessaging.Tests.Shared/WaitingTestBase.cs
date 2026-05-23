using System.Collections.Concurrent;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Time.Testing;

namespace LocalSqsSnsMessaging.Tests;

[ApplyDefaultCategory]
public abstract class WaitingTestBase
{
    protected const string TimeBased = ApplyDefaultCategoryAttribute.TimeBasedCategoryName;
    protected TimeProvider TimeProvider = TimeProvider.System;

    /// <summary>
    /// True when the test run targets real AWS (the verification project sets this via
    /// the <c>USE_REAL_AWS=1</c> environment variable). Tests use it to lengthen waits,
    /// reach for long-polling, and stop assuming zero round-trip latency.
    /// </summary>
    protected static bool IsRealAwsMode =>
        string.Equals(Environment.GetEnvironmentVariable("USE_REAL_AWS"), "1", StringComparison.Ordinal);

    protected static TimeSpan DefaultShortWaitTime =>
        TimeSpan.FromMilliseconds(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ? 2_500 : 100
        );

    /// <summary>
    /// Per-test unique suffix used to namespace queue, topic, and tag names so that
    /// shared resources on real AWS don't leak state across tests. Initialised lazily
    /// the first time <see cref="UniqueName"/> is called.
    /// </summary>
    private string? _uniqueSuffix;

    /// <summary>
    /// Returns a namespaced version of the supplied logical name suitable for use as
    /// a queue or topic name. For in-memory and Floci runs, where each test gets a
    /// fresh bus/account, the name is returned unchanged. For real AWS the name is
    /// suffixed with a short, deterministic, test-unique token so concurrent and
    /// retried tests can't collide on shared infrastructure.
    /// </summary>
    protected string UniqueName(string baseName)
    {
        ArgumentNullException.ThrowIfNull(baseName);
        if (!IsRealAwsMode)
        {
            return baseName;
        }
        _uniqueSuffix ??= "-" + Guid.NewGuid().ToString("N")[..8];
        // Preserve the .fifo suffix so the AWS SDK still routes correctly.
        const string fifo = ".fifo";
        if (baseName.EndsWith(fifo, StringComparison.Ordinal))
        {
            return baseName[..^fifo.Length] + _uniqueSuffix + fifo;
        }
        return baseName + _uniqueSuffix;
    }

    /// <summary>
    /// Track a queue URL for teardown at the end of the test. No-op outside real AWS mode
    /// (the in-memory bus is discarded per-test anyway).
    /// </summary>
#pragma warning disable CA1054 // URL parameter typed as string for fluent integration with AWS SDK calls
    protected void TrackQueueForTeardown(string queueUrl)
#pragma warning restore CA1054
    {
        if (!IsRealAwsMode) return;
        _queuesToTeardown.Add(queueUrl);
    }

    /// <summary>
    /// Track a topic ARN for teardown at the end of the test.
    /// </summary>
    protected void TrackTopicForTeardown(string topicArn)
    {
        if (!IsRealAwsMode) return;
        _topicsToTeardown.Add(topicArn);
    }

    private readonly ConcurrentBag<string> _queuesToTeardown = [];
    private readonly ConcurrentBag<string> _topicsToTeardown = [];

    /// <summary>
    /// Set by the verification fixture so the cross-cutting teardown hook can reach the
    /// SDK clients without every test needing to wire them in. Tests that don't touch
    /// real AWS leave these null.
    /// </summary>
    protected IAmazonSQS? SqsForTeardown { get; set; }
    protected IAmazonSimpleNotificationService? SnsForTeardown { get; set; }

    [TUnit.Core.After(TUnit.Core.HookType.Test)]
    public async Task TeardownTrackedResources()
    {
        if (!IsRealAwsMode) return;

        // Subscriptions are owned by topics, so unsubscribe first, then drop topics,
        // then queues. None of the deletes throw on success or "already gone".
        if (SnsForTeardown is not null)
        {
            foreach (var topicArn in _topicsToTeardown)
            {
                try
                {
                    var subs = await SnsForTeardown.ListSubscriptionsByTopicAsync(
                        new ListSubscriptionsByTopicRequest { TopicArn = topicArn });
                    foreach (var sub in subs.Subscriptions ?? [])
                    {
                        if (sub.SubscriptionArn is not null
                            && !sub.SubscriptionArn.StartsWith("PendingConfirmation", StringComparison.Ordinal))
                        {
                            await SnsForTeardown.UnsubscribeAsync(
                                new UnsubscribeRequest { SubscriptionArn = sub.SubscriptionArn });
                        }
                    }
                    await SnsForTeardown.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn });
                }
#pragma warning disable CA1031 // Teardown is best-effort; failures must not surface as test errors.
                catch (Exception)
                {
                    // Swallowed intentionally — see pragma above.
                }
#pragma warning restore CA1031
            }
        }

        if (SqsForTeardown is not null)
        {
            foreach (var queueUrl in _queuesToTeardown)
            {
                try
                {
                    await SqsForTeardown.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = queueUrl });
                }
#pragma warning disable CA1031 // Teardown is best-effort; failures must not surface as test errors.
                catch (Exception)
                {
                    // Swallowed intentionally — see pragma above.
                }
#pragma warning restore CA1031
            }
        }
    }

    /// <summary>
    /// Drain a queue until <paramref name="expectedCount"/> messages have been collected
    /// or the timeout elapses. Real AWS <c>ReceiveMessage</c> often returns fewer messages
    /// than <c>MaxNumberOfMessages</c> on any single call even when more are available; the
    /// in-memory bus returns everything synchronously. This helper papers over that gap so
    /// tests can assert on the eventual total without baking in retry loops. Unlike
    /// <see cref="ReceiveAllMessagesAsync"/>, messages are NOT deleted after receipt.
    /// </summary>
#pragma warning disable CA1054 // queueUrl is a string throughout the AWS SDK call surface
    protected async Task<List<Message>> ReceiveAllAsync(
        IAmazonSQS sqs,
        string queueUrl,
        int expectedCount,
        TimeSpan? timeout = null,
        int? visibilityTimeout = null,
        CancellationToken cancellationToken = default)
#pragma warning restore CA1054
    {
        ArgumentNullException.ThrowIfNull(sqs);
        var deadline = TimeProvider.GetUtcNow() + (timeout ?? TimeSpan.FromSeconds(IsRealAwsMode ? 30 : 5));
        var collected = new List<Message>();
        while (collected.Count < expectedCount && TimeProvider.GetUtcNow() < deadline)
        {
            var request = new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = Math.Min(10, expectedCount - collected.Count),
                WaitTimeSeconds = IsRealAwsMode ? 5 : 0,
                MessageAttributeNames = ["All"],
                MessageSystemAttributeNames = ["All"]
            };
            if (visibilityTimeout.HasValue)
            {
                request.VisibilityTimeout = visibilityTimeout.Value;
            }
            var result = await sqs.ReceiveMessageAsync(request, cancellationToken);
            if (result.Messages is { Count: > 0 } batch)
            {
                collected.AddRange(batch);
            }
            else if (!IsRealAwsMode)
            {
                // In-memory client returns everything in one call; an empty result means done.
                break;
            }
        }
        return collected;
    }

    /// <summary>
    /// Receives messages from a queue by polling until the expected count is reached or a timeout expires.
    /// Messages are deleted after receipt to unlock FIFO message groups for subsequent receives.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "Test helper method")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054:URI parameters should not be strings", Justification = "AWS SDK uses string URLs")]
    protected static async Task<List<Message>> ReceiveAllMessagesAsync(
        IAmazonSQS sqs,
        string queueUrl,
        int expectedCount,
        CancellationToken cancellationToken,
        List<string>? messageSystemAttributeNames = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(sqs);

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        var allMessages = new List<Message>();

        while (allMessages.Count < expectedCount && DateTime.UtcNow < deadline)
        {
            var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = Math.Min(10, expectedCount - allMessages.Count),
                MessageSystemAttributeNames = messageSystemAttributeNames ?? [],
                WaitTimeSeconds = 1
            }, cancellationToken);

            foreach (var msg in response.Messages)
            {
                allMessages.Add(msg);
                await sqs.DeleteMessageAsync(queueUrl, msg.ReceiptHandle, cancellationToken);
            }
        }

        return allMessages;
    }

    protected Task WaitAsync(TimeSpan timeSpan)
    {
        var categories = TestContext.Current?.Metadata.TestDetails.Categories;
        if (categories is null || !categories.Contains(TimeBased, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This method should only be called for tests marked with the 'TimeBased' category.");
        }

        if (TimeProvider is FakeTimeProvider fakeTimeProvider)
        {
            fakeTimeProvider.Advance(timeSpan);
            return Task.CompletedTask;
        }

        return Task.Delay(timeSpan, TimeProvider);
    }
}
