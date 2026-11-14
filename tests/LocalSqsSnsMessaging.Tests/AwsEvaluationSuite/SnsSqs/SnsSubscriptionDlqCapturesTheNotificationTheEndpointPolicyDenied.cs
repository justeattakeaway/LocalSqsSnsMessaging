// Translated from behavioral/sns__sqs/check_sns_subscription_dlq_captures_the_notification_the_endpoint_policy_denied.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.SnsSqs;

/// <summary>sns__sqs.sns-subscription-dlq-captures-the-notification-the-endpoint-policy-denied</summary>
[Category("AwsEvaluationSuite")]
public sealed class SnsSubscriptionDlqCapturesTheNotificationTheEndpointPolicyDenied
{
    private const string Scenario = "sns-subscription-dlq";

    private const string DenySid = "sim2realDenySnsSend";

    /// <summary>
    /// With the endpoint queue's policy denying SNS, a subscription RedrivePolicy captures the
    /// undelivered notification: it is absent at the endpoint and present in the dead-letter queue,
    /// and once the policy is restored a new message reaches the endpoint and not the DLQ.
    /// </summary>
    [Test]
    [DisplayName("sns__sqs.sns-subscription-dlq-captures-the-notification-the-endpoint-policy-denied")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsDeliveryFailures, "SNS delivery ignores a Deny for sns.amazonaws.com in the endpoint queue's policy, so the notification is delivered and never reaches the subscription's dead-letter queue")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topicArn = await CreateTopic(ctx);
        var (endpointUrl, endpointArn) = await CreateQueueForTopic(ctx, topicArn);
        var (dlqUrl, dlqArn) = await CreateQueueForTopic(ctx, topicArn);

        var subArn = await SubscribeAndConfirm(ctx, topicArn, endpointArn, attributes: new() { ["RawMessageDelivery"] = "true" });
        var redrive = new JsonObject { ["deadLetterTargetArn"] = dlqArn };
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn,
            AttributeName = "RedrivePolicy",
            AttributeValue = $$"""{"deadLetterTargetArn": "{{dlqArn}}"}""",
        });
        var stored = await PollUntil(
            ctx,
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("RedrivePolicy"),
            raw => !string.IsNullOrEmpty(raw) && JsonNode.DeepEquals(JsonNode.Parse(raw), redrive),
            timeoutS: 60.0,
            intervalS: 2.0,
            description: "GetSubscriptionAttributes reflects the subscription RedrivePolicy");
        Ensure((string?)JsonNode.Parse(stored!)!["deadLetterTargetArn"] == dlqArn,
            $"expected the stored RedrivePolicy to target '{dlqArn}', got '{stored}'");

        // ---- beat 1: control. The whole path works, L is measured, and the DLQ stays empty -----
        var healthy = FidelityContext.UniqueName("sim2real-behav-snsdlq-healthy-");
        var started = Monotonic();
        await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = healthy });
        var seen = await AwaitBodies(
            ctx, endpointUrl, [healthy], timeoutS: 90.0,
            description: "control: SNS must deliver to the endpoint queue while its policy only " +
                "Allows, or the dead-lettering below would be indistinguishable from " +
                "broken wiring");
        var latency = Math.Max(seen[healthy] - started, 1.0);
        var quiet = Math.Max(3.0 * latency, 5.0);
        var dlqDuringControl = await Drain(ctx, dlqUrl, windowS: quiet);
        Ensure(dlqDuringControl.Count == 0,
            "a SUCCESSFUL delivery must not also be copied to the dead-letter queue; the DLQ " +
            $"received {Repr(dlqDuringControl)} during a {F(quiet, "F0")}s window (>=3x the measured " +
            $"delivery latency of {F(latency, "F1")}s) after the healthy message had already arrived");

        // ---- beat 2: break exactly one thing — deny SNS on the ENDPOINT queue only -------------
        await AddDeny(ctx, endpointUrl, endpointArn);

        // ---- beat 3: the producer's call still succeeds ----------------------------------------
        var dead = FidelityContext.UniqueName("sim2real-behav-snsdlq-dead-");
        var resp = await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = dead });
        Ensure(!string.IsNullOrEmpty(resp.MessageId),
            "Publish must still succeed and mint a MessageId even though the destination now " +
            "denies delivery — SNS does not evaluate the endpoint's policy synchronously; got " +
            $"{{'MessageId': '{resp.MessageId}'}}");

        // ---- beat 4: the NEW leg — the undeliverable notification is recorded at the DLQ -------
        var dlqBody = await AwaitContaining(
            ctx, dlqUrl, dead, timeoutS: 180.0,
            description: "the subscription RedrivePolicy must capture the notification SNS could " +
                "not deliver: this is the only signal that separates 'correctly denied' " +
                "from 'never sent'");
        // `dead in dlq_body` holds by construction — _await_containing returns only a body that
        // contains it, and RAISES otherwise — so it is deliberately not re-asserted. What can
        // fail here is the DLQ carrying the WRONG message.
        Ensure(!dlqBody.Contains(healthy, StringComparison.Ordinal),
            "the DLQ must carry the UNDELIVERED message, not a replay of the healthy one; the " +
            $"body contained '{healthy}': '{dlqBody}'");

        // ---- beat 5: effect absent at the original destination ---------------------------------
        var leaked = await Drain(ctx, endpointUrl, windowS: quiet);
        Ensure(leaked.Count == 0,
            $"the denied message must not reach the endpoint queue: it received {Repr(leaked)} during " +
            $"a {F(quiet, "F0")}s window (>=3x the measured latency of {F(latency, "F1")}s) that began after " +
            "the same message had already been observed in the dead-letter queue");
        var approx = int.Parse((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = endpointUrl,
            AttributeNames = ["ApproximateNumberOfMessages"],
        })).Attributes["ApproximateNumberOfMessages"], CultureInfo.InvariantCulture);
        Ensure(approx == 0,
            "second independent observable for the absence: expected ApproximateNumberOfMessages " +
            $"== 0 on the endpoint queue, got {approx}");

        // ---- beat 6: restore, and delivery goes to the endpoint and NOT to the DLQ -------------
        await RemoveDeny(ctx, endpointUrl, endpointArn);
        var restored = FidelityContext.UniqueName("sim2real-behav-snsdlq-restored-");
        await RepublishUntilDelivered(
            ctx, topicArn, endpointUrl, restored,
            attempts: 2, perAttemptS: Math.Max(30.0, 3.0 * latency),
            description: "restore: with the Deny removed, the endpoint queue must receive again — " +
                "this proves the DLQ capture was caused by the policy and not by a " +
                "permanently broken subscription");
        var dlqAfterRestore = await Drain(ctx, dlqUrl, windowS: quiet);
        Ensure(dlqAfterRestore.Count == 0,
            "once delivery works again nothing further may be dead-lettered; the DLQ received " +
            $"{Repr(dlqAfterRestore)} in a {F(quiet, "F0")}s window after the restored message arrived at " +
            "the endpoint");
    }

    // ===== helpers =====

    /// <summary>time.monotonic(), on the check's virtual clock.</summary>
    private static double Monotonic() => FidelityTime.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private static string F(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(s => $"'{s}'")) + "]";

    private static string Head(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>Replace a queue policy and poll an independent read until it reflects the change.</summary>
    private static async Task SetQueuePolicy(FidelityContext ctx, string queueUrl, JsonObject policy, string? expectSid, bool present)
    {
        var sqs = ctx.Sqs;
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queueUrl, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        if (expectSid is null)
        {
            return;
        }

        await PollUntil(
            ctx,
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["Policy"] }))
                .Attributes?.GetValueOrDefault("Policy", "") ?? "",
            stored => stored.Contains(expectSid, StringComparison.Ordinal) == present,
            timeoutS: 45.0,
            intervalS: 3.0,
            description: $"the queue policy reports {expectSid} {(present ? "present" : "absent")}");
    }

    /// <summary>
    /// Add an explicit Deny alongside the still-present Allow. Exactly one thing changes.
    /// This Deny stops SNS delivery; this check asserts where the stopped message then goes.
    /// </summary>
    private static async Task AddDeny(FidelityContext ctx, string queueUrl, string queueArn)
    {
        var sqs = ctx.Sqs;
        var current = JsonNode.Parse((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["Policy"] }))
            .Attributes["Policy"])!.AsObject();
        var statements = current["Statement"]!.AsArray();
        statements.Add(new JsonObject
        {
            ["Sid"] = DenySid,
            ["Effect"] = "Deny",
            ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
            ["Action"] = "sqs:SendMessage",
            ["Resource"] = queueArn,
        });
        await SetQueuePolicy(ctx, queueUrl, current, expectSid: DenySid, present: true);
    }

    /// <summary>Drop the Deny statement and nothing else, then wait for the read to agree.</summary>
    private static async Task RemoveDeny(FidelityContext ctx, string queueUrl, string queueArn)
    {
        var sqs = ctx.Sqs;
        var current = JsonNode.Parse((await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["Policy"] }))
            .Attributes["Policy"])!.AsObject();
        var kept = current["Statement"]!.AsArray()
            .Where(s => (string?)s?["Sid"] != DenySid)
            .Select(s => s?.DeepClone())
            .ToArray();
        current["Statement"] = new JsonArray(kept);
        Ensure(kept.Length > 0,
            "removing the Deny must leave the original Allow in place, not an empty policy; the " +
            $"queue policy for {queueArn} would have no statements left");
        await SetQueuePolicy(ctx, queueUrl, current, expectSid: DenySid, present: false);
    }

    /// <summary>
    /// Long-poll until a body containing `needle` arrives; RAISE on the deadline.
    /// Returns the whole body so the caller can assert on it and so a failure prints the real
    /// shape — the exact envelope SNS writes to a subscription DLQ is one of this file's
    /// hypotheses.
    /// </summary>
    private static async Task<string> AwaitContaining(FidelityContext ctx, string queueUrl, string needle, double timeoutS, string description)
    {
        var sqs = ctx.Sqs;
        var others = new List<string>();
        var deadline = FidelityTime.StartNew();
        var budget = TimeSpan.FromSeconds(BudgetS(ctx, timeoutS));
        while (true)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var body = msg.Body;
                if (body.Contains(needle, StringComparison.Ordinal))
                {
                    return body;
                }

                others.Add(Head(body, 120));
            }

            if (deadline.Elapsed >= budget)
            {
                throw TimedOut(ctx,
                    $"{description}: no dead-letter queue body containing '{needle}' arrived " +
                    $"within {timeoutS}s; bodies seen: {Repr(others)}");
            }
        }
    }

    /// <summary>
    /// Publish the same marker up to `attempts` times, draining between attempts, and RAISE if
    /// it never lands. A queue-policy change is eventually consistent on SNS's delivery path, so
    /// the bounded republish is the retry that makes the restored-path assertion correct rather
    /// than racy; the marker is constant, so the assertion stays exact.
    /// </summary>
    private static async Task RepublishUntilDelivered(FidelityContext ctx, string topicArn, string queueUrl, string marker,
        int attempts, double perAttemptS, string description)
    {
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var seen = new List<string>();
        for (var i = 0; i < attempts; i++)
        {
            await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = marker });
            var deadline = FidelityTime.StartNew();
            var budget = TimeSpan.FromSeconds(BudgetS(ctx, perAttemptS));
            while (deadline.Elapsed < budget)
            {
                var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
                foreach (var msg in resp.Messages ?? [])
                {
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                    seen.Add(msg.Body);
                }

                if (seen.Contains(marker))
                {
                    return;
                }
            }
        }

        throw TimedOut(ctx,
            $"{description}: '{marker}' was never delivered across {attempts} republishes of " +
            $"{F(perAttemptS, "F0")}s each; bodies seen: {Repr(seen)}");
    }

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, Dictionary<string, string>? attributes = null)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = name,
            Attributes = new Dictionary<string, string>(attributes ?? []),
            Tags = TagsKv(),
        })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic publish to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => ctx.Sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topicArn } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (url, arn);
    }

    /// <summary>Subscribe an SQS queue and poll GetSubscriptionAttributes until it is confirmed.</summary>
    private static async Task<string> SubscribeAndConfirm(FidelityContext ctx, string topicArn, string queueArn,
        Dictionary<string, string>? attributes = null, double timeoutS = 60.0)
    {
        var sns = ctx.Sns;
        var request = new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn, ReturnSubscriptionArn = true };
        if (attributes is { Count: > 0 })
        {
            request.Attributes = attributes;
        }

        var subArn = (await sns.SubscribeAsync(request)).SubscriptionArn;
        ctx.OnCleanup(() => ctx.Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        await PollUntil(
            ctx,
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("PendingConfirmation"),
            pending => pending == "false",
            timeoutS: timeoutS,
            intervalS: 2.0,
            description: $"subscription {subArn} leaves PendingConfirmation");
        return subArn;
    }

    /// <summary>A wait's timeout: cap_s, bounded by the runner's remaining time less 5 s.</summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>True when the runner's remaining time, not the wait's own deadline, ended a capped wait.</summary>
    private static bool BudgetSpent(FidelityContext ctx) => ctx.TimeLeftSeconds <= 6.0;

    /// <summary>TimeoutError (not graded) when the run budget ended the wait, else AssertionError.</summary>
    private static Exception TimedOut(FidelityContext ctx, string message) =>
        BudgetSpent(ctx)
            ? new TimeoutException($"run budget spent before the wait ended: {message}")
            : Fail(message);

    /// <summary>Bounded retry; RAISES on the deadline so a timeout can never read as a pass.</summary>
    private static async Task<T> PollUntil<T>(FidelityContext ctx, Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS, string description)
    {
        var deadline = FidelityTime.StartNew();
        var budget = TimeSpan.FromSeconds(BudgetS(ctx, timeoutS));
        var value = await fn();
        while (!pred(value))
        {
            if (deadline.Elapsed >= budget)
            {
                throw TimedOut(ctx, $"{description}: not satisfied within {timeoutS}s, last value '{value}'");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            value = await fn();
        }

        return value;
    }

    /// <summary>
    /// Long-poll until every wanted body is seen; RAISES on the deadline.
    /// Returns {body: monotonic time it was first seen} so a caller can measure the real
    /// latency of this wiring and calibrate an absence window from it. A body in `forbidden`
    /// that arrives raises immediately: it is a leak, and swallowing it would hide the very
    /// thing the caller is about to assert.
    /// </summary>
    private static async Task<Dictionary<string, double>> AwaitBodies(FidelityContext ctx, string queueUrl, HashSet<string> wanted,
        double timeoutS, string description, HashSet<string>? forbidden = null)
    {
        var sqs = ctx.Sqs;
        var seen = new Dictionary<string, double>();
        var outstanding = new HashSet<string>(wanted);
        var deadline = FidelityTime.StartNew();
        var budget = TimeSpan.FromSeconds(BudgetS(ctx, timeoutS));
        while (outstanding.Count > 0)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            // The empty default only ever means "poll again"; the deadline below RAISES, so an
            // absence can never pass.
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var body = msg.Body;
                if (forbidden is { Count: > 0 } && forbidden.Contains(body))
                {
                    throw Fail($"{description}: a body that must never be delivered arrived: '{body}'");
                }

                if (outstanding.Remove(body))
                {
                    seen[body] = Monotonic();
                }
            }

            if (outstanding.Count > 0 && deadline.Elapsed >= budget)
            {
                throw TimedOut(ctx, $"{description}: {Repr(outstanding.Order(StringComparer.Ordinal))} never arrived within {timeoutS}s");
            }
        }

        return seen;
    }

    /// <summary>
    /// Long-poll for the whole window and return EVERY body seen, so the caller can assert
    /// on leakage rather than discard it. The empty default is correct here: it feeds an
    /// equality-to-empty assertion. A window the run budget cannot hold is not graded.
    /// </summary>
    private static async Task<List<string>> Drain(FidelityContext ctx, string queueUrl, double windowS)
    {
        var left = ctx.TimeLeftSeconds;
        if (windowS > left - 5.0)
        {
            throw new TimeoutException($"run budget spent: a {F(windowS, "F0")}s absence window does not fit the {F(left, "F0")}s left");
        }

        var sqs = ctx.Sqs;
        var bodies = new List<string>();
        var deadline = FidelityTime.StartNew();
        var window = TimeSpan.FromSeconds(windowS);
        while (deadline.Elapsed < window)
        {
            var waitS = Math.Max(1, Math.Min(5, (int)(window - deadline.Elapsed).TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = waitS });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                bodies.Add(msg.Body);
            }
        }

        return bodies;
    }
}
