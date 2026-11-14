// Translated from behavioral/sqs/check_sse_rejects_anonymous_sendmessage_that_an_unencrypted_queue_accepts.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.sse-rejects-anonymous-sendmessage-that-an-unencrypted-queue-accepts</summary>
[Category("AwsEvaluationSuite")]
public sealed class SseRejectsAnonymousSendmessageThatAnUnencryptedQueueAccepts
{
    private const string Check = "sse-rejects-anonymous-sendmessage-that-an-unencrypted-queue-accepts";

    private static readonly (string Code, string Message) Denied = (
        "AccessDenied",
        "Access to the resource The specified queue does not exist or you do not have access to " +
        "it. is denied.");

    /// <summary>
    /// A Principal * sqs:SendMessage grant, scoped to the caller's own IP, lets an UNSIGNED
    /// SendMessage land on an unencrypted queue; the identical call against an SSE-KMS queue
    /// carrying the same grant is refused and enqueues nothing.
    /// </summary>
    [Test]
    [DisplayName("sqs.sse-rejects-anonymous-sendmessage-that-an-unencrypted-queue-accepts")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.Validation, "expected an unsigned SendMessage to an SSE-KMS queue to be denied with AccessDenied, but it succeeded")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var cidrs = IngressCidrs();

        var plainUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-anon-plain-"),
            Attributes = new() { ["SqsManagedSseEnabled"] = "false" },
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(plainUrl));
        var plainArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = plainUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

        var sseUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-anon-sse-"),
            Attributes = new() { ["KmsMasterKeyId"] = "alias/aws/sqs" },
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(sseUrl));
        var sseArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = sseUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

        await SetPolicyAndWait(sqs, plainUrl, AnonSendPolicy(plainArn, cidrs));
        await SetPolicyAndWait(sqs, sseUrl, AnonSendPolicy(sseArn, cidrs));

        var unsignedSqs = ctx.Client(unsigned: true);

        // positive control: the grant is IP-scoped and short-lived (removed with the queue in
        // cleanup); once it has propagated, the identical unsigned call succeeds on the
        // unencrypted queue.
        var body = FidelityContext.UniqueName("unsigned-body-");
        var sent = await UntilAllowed(() => unsignedSqs.SendMessageAsync(new SendMessageRequest { QueueUrl = plainUrl, MessageBody = body }));
        Ensure(!string.IsNullOrEmpty(sent.MessageId), "the allowed unsigned SendMessage returned no MessageId");

        // effect proven: the message really landed, with the body the unsigned caller sent.
        var received = await ReceiveOne(sqs, plainUrl, timeoutSeconds: 30.0);
        Ensure(received.Body == body, $"expected body '{body}' on the plain queue, got '{received.Body}'");
        await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = plainUrl, ReceiptHandle = received.ReceiptHandle });

        // negative: the identical unsigned call against the SSE-KMS queue, carrying the same
        // Principal * grant, is refused -- SSE blocks the caller the plain queue just admitted.
        var err = await ExpectDenied(() => unsignedSqs.SendMessageAsync(new SendMessageRequest { QueueUrl = sseUrl, MessageBody = "unsigned-sse-body" }));
        Ensure((err.Code, err.Message) == Denied, $"got {err}");

        // effect absent: nothing was enqueued on the SSE queue by the refused attempt.
        var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = sseUrl,
            AttributeNames = ["ApproximateNumberOfMessages"],
        })).Attributes!;
        Ensure(attrs["ApproximateNumberOfMessages"] == "0",
            $"expected the refused SSE queue to hold no messages, got ApproximateNumberOfMessages='{attrs["ApproximateNumberOfMessages"]}'");
    }

    /// <summary>
    /// <c>ctx.ingress_cidrs()</c>: the CIDRs the service sees this runner's requests arrive from.
    /// The context has no equivalent; in-process requests come from loopback.
    /// </summary>
    private static List<string> IngressCidrs() => ["127.0.0.1/32"];

    private static string AnonSendPolicy(string queueArn, List<string> cidrs)
        => new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray
            {
                new JsonObject
                {
                    ["Sid"] = "AllowScopedAnonymousSend",
                    ["Effect"] = "Allow",
                    ["Principal"] = "*",
                    ["Action"] = "sqs:SendMessage",
                    ["Resource"] = queueArn,
                    ["Condition"] = new JsonObject
                    {
                        ["IpAddress"] = new JsonObject { ["aws:SourceIp"] = new JsonArray([.. cidrs.Select(c => (JsonNode)c)]) },
                    },
                },
            },
        }.ToJsonString();

    /// <summary>
    /// A policy document with every scalar leaf wrapped as a one-element list -- AWS stores a
    /// single-element <c>aws:SourceIp</c> list back as a bare string, so the two forms must compare
    /// equal. A list of scalars sorts; a list of dicts (<c>Statement</c>) recurses and sorts by its
    /// own canonical form. Returned as a key-sorted JSON string so two canonical forms compare with ==.
    /// </summary>
    private static string Canon(JsonNode? value)
    {
        switch (value)
        {
            case JsonObject obj:
                return "{" + string.Join(",", obj.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => JsonValue.Create(kv.Key).ToJsonString() + ":" + Canon(kv.Value))) + "}";
            case JsonArray arr:
                if (arr.Count > 0 && arr[0] is JsonObject)
                {
                    return "[" + string.Join(",", arr.Select(Canon).Order(StringComparer.Ordinal)) + "]";
                }

                return "[" + string.Join(",", arr.Select(v => v?.ToJsonString() ?? "null").Order(StringComparer.Ordinal)) + "]";
            default:
                return "[" + (value?.ToJsonString() ?? "null") + "]";
        }
    }

    /// <summary>
    /// SetQueueAttributes(Policy), then poll GetQueueAttributes until the control plane read
    /// confirms the same document -- a SetQueueAttributes 200 does not mean the read path agrees
    /// yet.
    /// </summary>
    private static async Task SetPolicyAndWait(AmazonSQSClient sqs, string queueUrl, string policyDoc, double timeoutSeconds = 60.0, double intervalSeconds = 3.0)
    {
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queueUrl, Attributes = new() { ["Policy"] = policyDoc } });
        var wanted = Canon(JsonNode.Parse(policyDoc));
        var deadline = FidelityTime.StartNew();
        while (true)
        {
            var current = ((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = queueUrl,
                AttributeNames = ["Policy"],
            })).Attributes ?? throw new KeyNotFoundException("'Attributes'")).GetValueOrDefault("Policy");
            if (current is not null && Canon(JsonNode.Parse(current)) == wanted)
            {
                return;
            }

            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw Fail($"queue policy on {queueUrl} never read back as set");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }
    }

    /// <summary>
    /// Call <paramref name="fn"/> until it stops raising the anonymous-denied error (policy propagation),
    /// bounded; any other failure raises immediately.
    /// </summary>
    private static async Task<T> UntilAllowed<T>(Func<Task<T>> fn, double timeoutSeconds = 90.0, double intervalSeconds = 3.0)
    {
        var deadline = FidelityTime.StartNew();
        AwsError? last = null;
        while (true)
        {
            try
            {
                return await fn();
            }
            catch (AmazonServiceException exc)
            {
                if ((exc.ErrorCode, exc.Message) != Denied)
                {
                    throw;
                }

                last = new AwsError(exc.ErrorCode, exc.Message, exc.StatusCode, exc);
            }

            if (deadline.Elapsed > TimeSpan.FromSeconds(timeoutSeconds))
            {
                throw Fail($"call never became allowed within {timeoutSeconds}s (last={last})");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }
    }

    /// <summary>Call fn(); return its botocore Error dict. An unexpected SUCCESS raises.</summary>
    private static async Task<AwsError> ExpectDenied(Func<Task<SendMessageResponse>> fn)
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return error;
        }

        throw Fail($"expected this unsigned SendMessage to be denied, but it SUCCEEDED and returned {{'MD5OfMessageBody': '{result!.MD5OfMessageBody}', 'MessageId': '{result.MessageId}'}}");
    }

    private static async Task<Message> ReceiveOne(AmazonSQSClient sqs, string url, double timeoutSeconds = 30.0, double intervalSeconds = 1.0)
    {
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 2 })).Messages ?? [];
            if (msgs.Count > 0)
            {
                return msgs[0];
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }

        throw Fail($"no message appeared on {url} within {timeoutSeconds}s");
    }
}
