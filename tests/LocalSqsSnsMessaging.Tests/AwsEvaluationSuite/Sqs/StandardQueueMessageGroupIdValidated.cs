// Translated from behavioral/sqs/check_standard_queue_message_group_id_validated.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.Runtime;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.standard-queue-message-group-id-validated</summary>
[Category("AwsEvaluationSuite")]
public sealed class StandardQueueMessageGroupIdValidated
{
    private const string Reason = "MessageGroupId can only include alphanumeric and punctuation characters. 1 to 128 in length.";

    /// <summary>
    /// A MessageGroupId on a standard queue must be 1 to 128 alphanumeric/punctuation characters:
    /// 128 characters and punctuation are accepted and surface verbatim on receive; 129 characters,
    /// an empty id and a space are refused, and nothing refused is enqueued.
    /// </summary>
    [Test]
    [DisplayName("sqs.standard-queue-message-group-id-validated")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Validation, "expected a 129-character MessageGroupId on a standard queue to be refused with InvalidParameterValue, but SendMessage succeeded")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-fairval-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var accepted = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["max-length"] = new string('g', 128),
            ["punctuation"] = "tenant:a/b-c_d.e!",
        };
        foreach (var (body, group) in accepted)
        {
            try
            {
                await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body, MessageGroupId = group });
            }
            catch (AmazonServiceException exc)
            {
                throw new FidelityAssertionException(
                    $"{body}: MessageGroupId '{group}' must be accepted on a standard queue, got " +
                    $"({exc.ErrorCode}, {exc.Message})", exc);
            }
        }

        foreach (var (label, group) in new[] { ("too-long", new string('g', 129)), ("empty", ""), ("space", "tenant a") })
        {
            var err = await Error(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = $"refused-{label}", MessageGroupId = group }));
            var want = $"Value {group} for parameter MessageGroupId is invalid. Reason: {Reason}";
            Ensure(err.Code == "InvalidParameterValue",
                $"{label}: MessageGroupId of length {group.Length} gave code '{err.Code}'");
            Ensure(err.Message == want, $"{label}: expected message '{want}', got '{err.Message}'");
        }

        var seen = new Dictionary<string, string?>(StringComparer.Ordinal);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 2,
                MessageSystemAttributeNames = ["MessageGroupId"],
            })).Messages ?? [];
            if (msgs.Count == 0 && seen.Count >= accepted.Count)
            {
                break;
            }

            foreach (var m in msgs)
            {
                seen[m.Body] = (m.Attributes ?? []).GetValueOrDefault("MessageGroupId");
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(seen.Count == accepted.Count && seen.All(kv => accepted.TryGetValue(kv.Key, out var v) && v == kv.Value),
            "the queue must hold exactly the accepted messages with their group ids verbatim; " +
            $"received {{{string.Join(", ", seen.Select(kv => $"'{kv.Key}': {(kv.Value is null ? "None" : $"'{kv.Value}'")}"))}}}");
    }

    /// <summary>
    /// The botocore Error dict <paramref name="fn"/> raises. An unexpected success raises too, naming what came
    /// back: a malformed group id that is accepted must not read as a code mismatch.
    /// </summary>
    private static async Task<AwsError> Error(Func<Task<SendMessageResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return error;
        }

        throw Fail($"expected the call to be refused, but it succeeded: {{'MD5OfMessageBody': '{result!.MD5OfMessageBody}', 'MessageId': '{result.MessageId}'}}");
    }
}
