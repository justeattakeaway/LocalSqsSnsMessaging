// Translated from behavioral/sqs/check_redrive_policy_dlq_type_mismatch_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.redrive-policy-dlq-type-mismatch-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class RedrivePolicyDlqTypeMismatchRejected
{
    // SetQueueAttributes refuses a cross-type pairing with a message echoing the policy verbatim.
    private const string ErrInvalidParam = "InvalidParameterValue";

    /// <summary>
    /// A RedrivePolicy pairing a standard queue with a FIFO DLQ, or the reverse, is refused with
    /// InvalidParameterValue and not stored, while same-type pairings are accepted and read back.
    /// </summary>
    [Test]
    [DisplayName("sqs.redrive-policy-dlq-type-mismatch-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.DeadLetterQueues, "a RedrivePolicy pairing a standard source with a FIFO DLQ (or the reverse) is accepted instead of refused")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;

        var stdSrc = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-std-src" })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(stdSrc));
        var stdDlq = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-std-dlq" })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(stdDlq));
        var fifoSrc = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-src.fifo",
            Attributes = new() { ["FifoQueue"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(fifoSrc));
        var fifoDlq = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + "-dlq.fifo",
            Attributes = new() { ["FifoQueue"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(fifoDlq));

        async Task<string> ArnOf(string url) =>
            (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

        var (stdDlqArn, fifoDlqArn) = (await ArnOf(stdDlq), await ArnOf(fifoDlq));
        Ensure(fifoDlqArn.EndsWith(".fifo", StringComparison.Ordinal), $"FIFO DLQ arn looks wrong: {fifoDlqArn}");
        Ensure(!stdDlqArn.EndsWith(".fifo", StringComparison.Ordinal), $"standard DLQ arn looks wrong: {stdDlqArn}");

        async Task<string?> RedrivePolicy(string url) =>
            // SQS omits the Attributes key entirely when none of the requested attributes is set.
            (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["RedrivePolicy"] }))
            .Attributes?.GetValueOrDefault("RedrivePolicy");

        // Both directions: an implementation may reject one mismatch and miss the other.
        foreach (var (label, srcUrl, dlqArn) in new[]
                 {
                     ("standard source -> FIFO DLQ", stdSrc, fifoDlqArn),
                     ("FIFO source -> standard DLQ", fifoSrc, stdDlqArn),
                 })
        {
            Ensure(await RedrivePolicy(srcUrl) is null, $"{label}: source already had a RedrivePolicy");
            var (code, message) = await Err(() => sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
            {
                QueueUrl = srcUrl,
                Attributes = new() { ["RedrivePolicy"] = Redrive(dlqArn) },
            }));
            Ensure(code == ErrInvalidParam,
                $"{label}: expected {ErrInvalidParam}, got '{code}' ('{message}')");
            Ensure(message == MismatchMessage(Redrive(dlqArn)),
                $"{label}: the refusal must name the queue-type rule, got '{message}'");
            // effect absent: nothing was stored, not even partially
            var stored = await RedrivePolicy(srcUrl);
            Ensure(stored is null,
                $"{label}: the rejected RedrivePolicy was stored anyway: {stored}");
        }

        // --- control: same-type pairings are accepted and read back exactly ----------------
        foreach (var (label, srcUrl, dlqArn) in new[]
                 {
                     ("standard source -> standard DLQ", stdSrc, stdDlqArn),
                     ("FIFO source -> FIFO DLQ", fifoSrc, fifoDlqArn),
                 })
        {
            await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
            {
                QueueUrl = srcUrl,
                Attributes = new() { ["RedrivePolicy"] = Redrive(dlqArn) },
            });
            var stored = await RedrivePolicy(srcUrl);
            Ensure(!string.IsNullOrEmpty(stored), $"{label}: valid RedrivePolicy was not stored");
            var parsed = JsonNode.Parse(stored)!;
            Ensure(Str(parsed["deadLetterTargetArn"]) == dlqArn,
                $"{label}: stored deadLetterTargetArn {parsed["deadLetterTargetArn"]?.ToJsonString()} "
                + $"!= '{dlqArn}'");
            Ensure(PyInt(parsed["maxReceiveCount"]) == 3,
                $"{label}: stored maxReceiveCount {parsed["maxReceiveCount"]?.ToJsonString()} != 3");
        }
    }

    /// <summary>
    /// Exact message, so an InvalidParameterValue for an unrelated reason cannot pass. The doubled
    /// period is what AWS returns.
    /// </summary>
    private static string MismatchMessage(string policy) =>
        $"Value {policy} for parameter RedrivePolicy is invalid. "
        + "Reason: Dead-letter queue must be same type of queue as the source..";

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

    /// <summary><c>json.dumps({"deadLetterTargetArn": arn, "maxReceiveCount": 3})</c>, byte for byte.</summary>
    private static string Redrive(string arn) =>
        $"{{\"deadLetterTargetArn\": {JsonValue.Create(arn).ToJsonString()}, \"maxReceiveCount\": 3}}";

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>Python's <c>int(value)</c> of a decoded JSON number or numeric string; raises otherwise.</summary>
    private static long PyInt(JsonNode? node) => node switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => (long)v.GetValue<double>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.String => long.Parse(v.GetValue<string>().Trim(), CultureInfo.InvariantCulture),
        _ => throw new InvalidCastException($"int() argument must be a string or a number, not {node?.GetValueKind().ToString() ?? "NoneType"}"),
    };
}
