// Translated from behavioral/sqs/check_queue_policy_explicit_deny_blocks_send.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Text.Json.Nodes;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.queue-policy-explicit-deny-blocks-send</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueuePolicyExplicitDenyBlocksSend
{
    // A resource-policy denial is HTTP 403 with Error.Code exactly "AccessDenied", not
    // AccessDeniedException.
    private const string ErrAccessDenied = "AccessDenied";

    // Adding a Deny to a queue that already has a policy can take ~120s to be enforced (removing it
    // is near-instant), so a short window misreads propagation as "policies are not evaluated".
    private const int DenyPropagationS = 300;

    /// <summary>
    /// An explicit Deny on sqs:SendMessage in the queue Policy blocks sends the identity allows, is
    /// scoped to that action, and removing it restores the send.
    ///
    /// Reference: https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_policies_evaluation-logic.html
    /// </summary>
    [Test]
    [DisplayName("sqs.queue-policy-explicit-deny-blocks-send")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Other, "an explicit Deny in the queue Policy is never enforced, so the probe loop outlasts the 60s budget")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-"),
            Tags = new() { ["sim2real:managed"] = "true", ["sim2real"] = "queue-policy-explicit-deny-blocks-send" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["QueueArn"],
        })).Attributes!["QueueArn"];
        var parts = arn.Split(':');
        Ensure(parts.Length >= 6 && parts[4].Length > 0, $"cannot read the account id out of '{arn}'");
        var account = parts[4];
        var root = $"arn:aws:iam::{account}:root";
        // The denial message names the calling principal; read it rather than assume it.
        // (No STS client here: the bus's caller is the account root.)
        var callerArn = $"arn:aws:iam::{ctx.Account}:root";

        JsonObject AllowStmt() => new()
        {
            ["Sid"] = "AllowAccount",
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["AWS"] = root },
            ["Action"] = "SQS:*",
            ["Resource"] = arn,
        };
        // Never widen to sqs:* -- a Deny on everything also blocks the owner's DeleteQueue.
        JsonObject DenyStmt() => new()
        {
            ["Sid"] = "DenySend",
            ["Effect"] = "Deny",
            ["Principal"] = new JsonObject { ["AWS"] = root },
            ["Action"] = "sqs:SendMessage",
            ["Resource"] = arn,
        };

        Task SetPolicy(params JsonObject[] statements) => sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new()
            {
                ["Policy"] = new JsonObject
                {
                    ["Version"] = "2012-10-17",
                    ["Id"] = $"{arn}/sim2real-{Guid.NewGuid().ToString("N")[..8]}",
                    ["Statement"] = new JsonArray(statements),
                }.ToJsonString(),
            },
        });

        var token = Guid.NewGuid().ToString("N")[..8];

        // --- full valid setup: allow-only policy, and a send that works -------------------
        await SetPolicy(AllowStmt());
        // The allow-only policy must not block SendMessage, or the deny leg below proves nothing.
        await UntilSent(sqs, url, $"baseline-{token}", timeoutS: 30);
        Ensure((await Drain(sqs, url, expect: 1, timeoutS: 20)).SequenceEqual([$"baseline-{token}"]),
            "baseline message was not delivered");

        // --- break exactly one thing: add an explicit Deny on SendMessage -----------------
        await SetPolicy(AllowStmt(), DenyStmt());
        // Each probe send carries its OWN body, so the sends that land during the propagation window
        // can be accounted for exactly and drained; only the send made once the Deny is demonstrably
        // in effect is used for the effect-absent leg below.
        List<string> accepted = [];
        string? code = null;
        string? message = null;
        var deadline = Stopwatch.StartNew();
        var attempt = 0;
        while (true)
        {
            attempt++;
            var probe = $"probe-{attempt}-{token}";
            var err = await FidelityContext.CaptureError(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = probe }));
            if (err is not null)
            {
                (code, message) = (err.Code, err.Message ?? "");
                break;
            }

            accepted.Add(probe);
            if (deadline.Elapsed >= TimeSpan.FromSeconds(DenyPropagationS))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(5.0), cancellationToken);
        }

        Ensure(code == ErrAccessDenied,
            "an explicit Deny on sqs:SendMessage did not block the send within "
            + $"{DenyPropagationS}s and {attempt} attempts: got {code ?? "None"} "
            + "(None means the send succeeded)");
        Ensure(message == DenyMessage(callerArn, arn),
            $"the 403 must be attributed to the resource policy, got '{message}'");
        // Sends made before the Deny was enforced must have enqueued -- exactly them and nothing
        // else. This clears the queue so the denied send below has an empty queue to be absent from.
        if (accepted.Count > 0)
        {
            var landed = await Drain(sqs, url, expect: accepted.Count, timeoutS: 60);
            var sortedAccepted = accepted.Order(StringComparer.Ordinal).ToList();
            Ensure(landed.SequenceEqual(sortedAccepted),
                $"the {accepted.Count} send(s) accepted before the Deny took effect must all be "
                + $"delivered and nothing else: [{string.Join(", ", landed)}] != [{string.Join(", ", sortedAccepted)}]");
        }

        // --- effect absent: a send made with the Deny in force leaves nothing behind --------
        var (deniedCode, deniedMessage) = await Err(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = $"denied-{token}" }));
        Ensure(deniedCode == ErrAccessDenied,
            $"the Deny stopped being enforced between two consecutive sends: {deniedCode}");
        Ensure(deniedMessage == DenyMessage(callerArn, arn), $"got '{deniedMessage}'");
        // The counter needs a moment to reflect the drain above, so wait for it to settle on 0
        // rather than reading it immediately; the drain below is the stronger absence evidence.
        var depth = await Poll(
            async () => (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!.GetValueOrDefault("ApproximateNumberOfMessages", "?"),
            v => v == "0",
            timeoutS: 30);
        Ensure(depth == "0", $"denied send enqueued something: ApproximateNumberOfMessages={depth}");
        var leaked = await Drain(sqs, url, expect: 1, timeoutS: 10);
        Ensure(leaked.Count == 0, $"denied send delivered [{string.Join(", ", leaked)}]");

        // --- the Deny is action-scoped: other actions on the same queue still work --------
        var recv = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 1, WaitTimeSeconds = 1 });
        Ensure((recv.Messages ?? []).Count == 0, $"unexpected message present: {recv.Messages?.Count}");
        var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["QueueArn"],
        })).Attributes!;
        Ensure(attrs.GetValueOrDefault("QueueArn") == arn,
            $"GetQueueAttributes was collaterally denied or changed: {string.Join(", ", attrs)}");

        // --- control: remove the Deny and the identical send succeeds again ---------------
        await SetPolicy(AllowStmt());
        await UntilSent(sqs, url, $"restored-{token}", timeoutS: DenyPropagationS, intervalS: 5.0);
        Ensure((await Drain(sqs, url, expect: 1, timeoutS: 20)).SequenceEqual([$"restored-{token}"]),
            "control message was not delivered after the Deny was removed");
    }

    /// <summary>
    /// The exact denial message, which attributes the 403 to the resource policy, so an
    /// AccessDenied raised for an unrelated reason cannot satisfy the leg.
    /// </summary>
    private static string DenyMessage(string callerArn, string queueArn) =>
        $"User: {callerArn} is not authorized to perform: sqs:sendmessage on resource: "
        + $"{queueArn} with an explicit deny in a resource-based policy";

    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS = 1.0)
    {
        var deadline = Stopwatch.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                return val;
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalS));
            val = await fn();
        }

        return val;
    }

    /// <summary>
    /// Return (Error.Code, Error.Message) for the ClientError fn raises; an unexpected success
    /// raises, so an accepted send is not reported as a mere code mismatch.
    /// </summary>
    private static async Task<(string? Code, string Message)> Err<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be denied, but it SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message ?? "");
    }

    /// <summary>Send <paramref name="body"/> until SendMessage accepts it; a refusal that outlasts the deadline raises.</summary>
    private static async Task<string> UntilSent(IAmazonSQS sqs, string url, string body, double timeoutS, double intervalS = 1.0)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var (resp, error) = await FidelityContext.Capture(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = body }));
            if (error is null)
            {
                return resp!.MessageId;
            }

            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw new FidelityAssertionException(
                    $"SendMessage was still refused after {timeoutS}s: {error}", error.Exception);
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalS));
        }
    }

    private static async Task<List<string>> Drain(IAmazonSQS sqs, string url, int expect, double timeoutS)
    {
        Dictionary<string, string> seen = [];
        var deadline = Stopwatch.StartNew();
        while (seen.Count < expect && deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
            foreach (var msg in resp.Messages ?? [])
            {
                seen[msg.MessageId] = msg.Body;
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        return seen.Values.Order(StringComparer.Ordinal).ToList();
    }
}
