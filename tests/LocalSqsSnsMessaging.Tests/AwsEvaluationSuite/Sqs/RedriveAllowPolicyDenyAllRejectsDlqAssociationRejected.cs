// Translated from behavioral/sqs/check_redrive_allow_policy_deny_all_rejects_dlq_association_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Text.Json.Nodes;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.redrive-allow-policy-deny-all-rejects-dlq-association-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class RedriveAllowPolicyDenyAllRejectsDlqAssociationRejected
{
    // SQS returns the query-style code "InvalidParameterValue" here, which the service model does not
    // declare (it models InvalidAttributeValue instead).
    private const string ErrInvalidParam = "InvalidParameterValue";

    private static Dictionary<string, string> Tags() => new() { ["sim2real:managed"] = "true", ["sim2real"] = "redrive-allow-policy-deny-all" };

    /// <summary>
    /// Naming a denyAll RedriveAllowPolicy queue as a DLQ fails InvalidParameterValue and stores
    /// no RedrivePolicy; after flipping it to allowAll the same call succeeds and redrives a message.
    ///
    /// Reference: https://docs.aws.amazon.com/AWSSimpleQueueService/latest/APIReference/API_SetQueueAttributes.html
    /// </summary>
    [Test]
    [DisplayName("sqs.redrive-allow-policy-deny-all-rejects-dlq-association-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.DeadLetterQueues, "naming a denyAll RedriveAllowPolicy queue as a dead-letter target is accepted instead of refused")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var marker = $"redrive-marker-{Guid.NewGuid().ToString("N")[..8]}";

        var urlDlq = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-dlq",
            Attributes = new() { ["RedriveAllowPolicy"] = new JsonObject { ["redrivePermission"] = "denyAll" }.ToJsonString() },
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
        Ensure(Str(stored["redrivePermission"]) == "denyAll",
            "the dead-letter queue never carried denyAll, so the refusal below would prove nothing: "
            + $"RedriveAllowPolicy={stored.ToJsonString()}");

        // VisibilityTimeout=1 so the redrive in the control leg happens in seconds rather than minutes.
        var urlSrc = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-src",
            Attributes = new() { ["VisibilityTimeout"] = "1" },
            Tags = Tags(),
        })).QueueUrl;
        // Registered before the DLQ association exists, and LIFO puts it first: the source is deleted
        // before the DLQ, so no redrive can push a message into the DLQ while it is being torn down.
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(urlSrc));

        // --- break exactly one thing: point the source at a denyAll dead-letter queue ------
        var (code, message) = await Err(() => sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = urlSrc,
            Attributes = new() { ["RedrivePolicy"] = Redrive(dlqArn) },
        }));
        Ensure(code == ErrInvalidParam,
            "naming a denyAll queue as a dead-letter target must be refused with "
            + $"'{ErrInvalidParam}', got '{code}'");
        Ensure(message == DenyMessage(dlqArn),
            "the refusal must be attributed to the dead-letter queue's RedriveAllowPolicy, got "
            + $"'{message}'");

        // --- effect absent: the refused association was not stored ------------------------
        var attrsAfterDenial = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = urlSrc,
            AttributeNames = ["All"],
        })).Attributes!;
        Ensure(!attrsAfterDenial.ContainsKey("RedrivePolicy"),
            "a refused association still stored a RedrivePolicy on the source queue: "
            + $"RedrivePolicy='{attrsAfterDenial.GetValueOrDefault("RedrivePolicy")}'");

        // --- control: repair the one broken thing, and the identical call must succeed -----
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = urlDlq,
            Attributes = new() { ["RedriveAllowPolicy"] = new JsonObject { ["redrivePermission"] = "allowAll" }.ToJsonString() },
        });
        // The allowAll change propagates eventually, so the identical call is retried within a bound.
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var err = await FidelityContext.CaptureError(() => sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
            {
                QueueUrl = urlSrc,
                Attributes = new() { ["RedrivePolicy"] = Redrive(dlqArn) },
            }));
            if (err is null)
            {
                break;
            }

            if (deadline.Elapsed >= TimeSpan.FromSeconds(90))
            {
                throw new FidelityAssertionException(
                    "flipping the dead-letter queue to allowAll did not permit the identical "
                    + $"association: still {err} -- the check cannot pass "
                    + "by refusing every association", err.Exception);
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        var attrsAfterAllow = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = urlSrc,
            AttributeNames = ["All"],
        })).Attributes!;
        Ensure(Str(JsonNode.Parse(attrsAfterAllow["RedrivePolicy"])!["deadLetterTargetArn"]) == dlqArn,
            "the permitted association did not store the dead-letter queue arn: "
            + $"RedrivePolicy='{attrsAfterAllow.GetValueOrDefault("RedrivePolicy")}'");

        // --- and the association it created is functional, not merely stored --------------
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = urlSrc, MessageBody = marker });
        List<string> dlqBodies = [];
        deadline = Stopwatch.StartNew();
        while (dlqBodies.Count == 0 && deadline.Elapsed < TimeSpan.FromSeconds(120))
        {
            // Receiving from the source without deleting is what drives the receive count past
            // maxReceiveCount=1 and triggers the redrive.
            await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = urlSrc, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = urlDlq, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
            foreach (var m in resp.Messages ?? [])
            {
                dlqBodies.Add(m.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = urlDlq, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(dlqBodies.SequenceEqual([marker]),
            "the association was stored but the dead-letter queue never received the redriven "
            + $"message: got [{string.Join(", ", dlqBodies)}], expected ['{marker}']");
    }

    /// <summary>
    /// The exact message, so an InvalidParameterValue raised for an unrelated reason (a malformed ARN,
    /// a bad maxReceiveCount, a cross-type pairing) cannot satisfy the negative.
    /// </summary>
    private static string DenyMessage(string dlqArn) =>
        $"Value RedrivePolicy(maxReceiveCount=1, deadLetterTargetArn={dlqArn}) for parameter "
        + "RedrivePolicy is invalid. Reason: The value for the dead-letter queue's RedriveAllowPolicy "
        + "attribute prevents you from completing this request.";

    /// <summary>Return (Error.Code, Error.Message) for the ClientError fn raises; a success raises.</summary>
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
