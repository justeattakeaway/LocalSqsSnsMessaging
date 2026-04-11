using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Time.Testing;

namespace LocalSqsSnsMessaging.Tests;

[ApplyDefaultCategory]
public abstract class WaitingTestBase
{
    protected const string TimeBased = ApplyDefaultCategoryAttribute.TimeBasedCategoryName;
    protected TimeProvider TimeProvider = TimeProvider.System;

    protected static TimeSpan DefaultShortWaitTime =>
        TimeSpan.FromMilliseconds(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ? 2_500 : 100
        );

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
}
