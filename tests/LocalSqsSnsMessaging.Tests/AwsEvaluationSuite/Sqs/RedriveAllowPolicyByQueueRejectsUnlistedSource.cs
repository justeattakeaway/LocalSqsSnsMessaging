// Translated from behavioral/sqs/check_redrive_allow_policy_by_queue_rejects_unlisted_source.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Text.Json.Nodes;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.redrive-allow-policy-by-queue-rejects-unlisted-source</summary>
[Category("AwsEvaluationSuite")]
public sealed class RedriveAllowPolicyByQueueRejectsUnlistedSource
{
    // SQS returns this same code and reason for denyAll; only the per-source decision differs.
    private const string ErrInvalidParam = "InvalidParameterValue";

    private static Dictionary<string, string> Tags() => new() { ["sim2real:managed"] = "true", ["sim2real"] = "redrive-allow-policy-by-queue" };

    /// <summary>
    /// A byQueue RedriveAllowPolicy is a per-source allowlist: an unlisted queue's RedrivePolicy is
    /// refused with InvalidParameterValue and not stored; the listed queue's is accepted and redrives.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-dead-letter-queues.html
    /// </summary>
    [Test]
    [DisplayName("sqs.redrive-allow-policy-by-queue-rejects-unlisted-source")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.DeadLetterQueues, "a RedrivePolicy from a source not in the DLQ's byQueue RedriveAllowPolicy is accepted instead of refused")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var marker = $"bq-marker-{Guid.NewGuid().ToString("N")[..8]}";

        // VisibilityTimeout=1 so the redrive in the allowed leg happens in seconds.
        var urlAllowed = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-allowed",
            Attributes = new() { ["VisibilityTimeout"] = "1" },
            Tags = Tags(),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(urlAllowed));
        var allowedArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = urlAllowed,
            AttributeNames = ["QueueArn"],
        })).Attributes!["QueueArn"];

        var urlUnlisted = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-unlisted",
            Attributes = new() { ["VisibilityTimeout"] = "1" },
            Tags = Tags(),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(urlUnlisted));

        // sourceQueueArns must name an existing ARN, so the listed source is created first.
        var urlDlq = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-dlq",
            Attributes = new()
            {
                ["RedriveAllowPolicy"] = new JsonObject
                {
                    ["redrivePermission"] = "byQueue",
                    ["sourceQueueArns"] = new JsonArray(allowedArn),
                }.ToJsonString(),
            },
            Tags = Tags(),
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(urlDlq));
        var dlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = urlDlq,
            AttributeNames = ["QueueArn"],
        })).Attributes!["QueueArn"];

        var stored = JsonNode.Parse((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = urlDlq,
            AttributeNames = ["All"],
        })).Attributes!.GetValueOrDefault("RedriveAllowPolicy", "{}"))!.AsObject();
        Ensure(Str(stored["redrivePermission"]) == "byQueue",
            "the dead-letter queue is not byQueue-scoped, so nothing below tests an allowlist: "
            + $"RedriveAllowPolicy={stored.ToJsonString()}");
        Ensure(JsonNode.DeepEquals(stored["sourceQueueArns"], new JsonArray(allowedArn)),
            "the allowlist does not name exactly the queue this check treats as allowed: "
            + $"sourceQueueArns={stored["sourceQueueArns"]?.ToJsonString() ?? "None"}, expected ['{allowedArn}']");

        // --- break exactly one thing: the SOURCE is a queue that is not on the allowlist ---
        var (code, message) = await Err(() => sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = urlUnlisted,
            Attributes = new() { ["RedrivePolicy"] = Redrive(dlqArn) },
        }));
        Ensure(code == ErrInvalidParam,
            $"a queue absent from a byQueue allowlist must be refused with '{ErrInvalidParam}' when "
            + $"it names that dead-letter queue, got '{code}'");
        Ensure(message == DenyMessage(dlqArn),
            "the refusal must be attributed to the dead-letter queue's RedriveAllowPolicy, got "
            + $"'{message}'");

        // --- effect absent -----------------------------------------------------------------
        var unlistedAttrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = urlUnlisted,
            AttributeNames = ["All"],
        })).Attributes!;
        Ensure(!unlistedAttrs.ContainsKey("RedrivePolicy"),
            "the refused association was still stored on the unlisted source queue: "
            + $"RedrivePolicy='{unlistedAttrs.GetValueOrDefault("RedrivePolicy")}'");

        // --- control: the ALLOWLISTED source makes the identical call, and it works --------
        var allowErr = await FidelityContext.CaptureError(() => sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = urlAllowed,
            Attributes = new() { ["RedrivePolicy"] = Redrive(dlqArn) },
        }));
        if (allowErr is not null)
        {
            throw new FidelityAssertionException(
                $"the queue named in sourceQueueArns was refused too ({allowErr}) "
                + "-- byQueue was evaluated as a blanket deny rather than as an allowlist", allowErr.Exception);
        }

        var allowedAttrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = urlAllowed,
            AttributeNames = ["All"],
        })).Attributes!;
        Ensure(Str(JsonNode.Parse(allowedAttrs["RedrivePolicy"])!["deadLetterTargetArn"]) == dlqArn,
            "the permitted association did not store the dead-letter queue arn: "
            + $"RedrivePolicy='{allowedAttrs.GetValueOrDefault("RedrivePolicy")}'");

        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = urlAllowed, MessageBody = marker });
        List<string> dlqBodies = [];
        var deadline = Stopwatch.StartNew();
        while (dlqBodies.Count == 0 && deadline.Elapsed < TimeSpan.FromSeconds(120))
        {
            // Receiving from the source without deleting drives the receive count past
            // maxReceiveCount=1, which is what triggers the redrive.
            await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = urlAllowed, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = urlDlq, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
            foreach (var m in resp.Messages ?? [])
            {
                dlqBodies.Add(m.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = urlDlq, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(dlqBodies.SequenceEqual([marker]),
            "the allowlisted association was stored but the dead-letter queue never received the "
            + $"redriven message: got [{string.Join(", ", dlqBodies)}], expected ['{marker}']");
    }

    private static string DenyMessage(string dlqArn) =>
        $"Value RedrivePolicy(maxReceiveCount=1, deadLetterTargetArn={dlqArn}) for parameter "
        + "RedrivePolicy is invalid. Reason: The value for the dead-letter queue's RedriveAllowPolicy "
        + "attribute prevents you from completing this request.";

    /// <summary>
    /// Return (Error.Code, Error.Message) for the ClientError fn raises. A success raises: an
    /// implementation accepting input real AWS rejects is the strongest signal here.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Err<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected a rejection, but the call SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }

    private static string Redrive(string arn) => new JsonObject { ["deadLetterTargetArn"] = arn, ["maxReceiveCount"] = 1 }.ToJsonString();

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : null;
}
