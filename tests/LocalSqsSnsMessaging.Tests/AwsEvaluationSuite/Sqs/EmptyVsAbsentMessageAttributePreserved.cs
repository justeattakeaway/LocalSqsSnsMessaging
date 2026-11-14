// Translated from behavioral/sqs/check_empty_vs_absent_message_attribute_preserved.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.empty-vs-absent-message-attribute-preserved</summary>
[Category("AwsEvaluationSuite")]
public sealed class EmptyVsAbsentMessageAttributePreserved
{
    private const string EmptyValueMessage = "Message (user) attribute 'Coupon' must contain a non-empty value of type 'String'.";

    private static readonly Dictionary<string, string> Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "empty-vs-absent-message-attribute-preserved" };

    /// <summary>
    /// SQS rejects an attribute with an explicit empty-string value as InvalidParameterValue; a send
    /// omitting the attribute receives with it absent, and a long poll after it never returns the rejected body.
    /// </summary>
    [Test]
    [DisplayName("sqs.empty-vs-absent-message-attribute-preserved")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-attr-"), Tags = new(Tags) })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var error = await ClientError(
            () => sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = url,
                MessageBody = "empty-attr-attempt",
                MessageAttributes = new() { ["Coupon"] = new() { DataType = "String", StringValue = "" } },
            }));
        Ensure(error.Code == "InvalidParameterValue",
            $"expected InvalidParameterValue, got {error.Code}");
        Ensure(error.Message == EmptyValueMessage,
            $"expected message {EmptyValueMessage}, got {error.Message}");

        // Positive control: omit the attribute entirely -> succeeds, attribute is absent on receive.
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "no-attr-message" });
        var received = await PollForMessage(ctx, url);
        Ensure(received.Body == "no-attr-message",
            $"expected body 'no-attr-message', got {received.Body}");
        var receivedAttrs = received.MessageAttributes ?? [];
        Ensure(!receivedAttrs.ContainsKey("Coupon"),
            "expected 'Coupon' absent (never sent), got present with " +
            $"{receivedAttrs.GetValueOrDefault("Coupon")?.StringValue}");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = received.ReceiptHandle });

        // Effect absent: the rejected send preceded the control, which is now received, so a long
        // poll (it consults every SQS server, unlike the approximate counters) would return it.
        var leftover = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
        var leftoverBodies = (leftover.Messages ?? []).Select(m => m.Body).ToList();
        Ensure(!leftoverBodies.Contains("empty-attr-attempt"),
            $"the rejected send reached the queue: a long poll after the control returned [{string.Join(", ", leftoverBodies)}]");
    }

    private static async Task<AwsError> ClientError<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return error;
        }

        throw Fail($"real AWS rejects this call, but it SUCCEEDED and returned {Repr(result)}");
    }

    private static async Task<Message> PollForMessage(FidelityContext ctx, string url, double timeoutSeconds = 30.0)
    {
        var sqs = ctx.Sqs;
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"],
            });
            var msgs = resp.Messages ?? [];
            if (msgs.Count > 0)
            {
                return msgs[0];
            }
        }

        throw Fail("no message received within timeout");
    }

    private static string Repr(object? value)
    {
        try
        {
            return JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object));
        }
        catch (Exception)
        {
            return value?.ToString() ?? "None";
        }
    }
}
