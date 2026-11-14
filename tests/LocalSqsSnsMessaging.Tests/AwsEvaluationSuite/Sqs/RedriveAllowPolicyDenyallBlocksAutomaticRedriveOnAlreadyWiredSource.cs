// Translated from behavioral/sqs/check_redrive_allow_policy_denyall_blocks_automatic_redrive_on_already_wired_source.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using System.Text.Json.Nodes;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.redrive-allow-policy-denyall-blocks-automatic-redrive-on-already-wired-source</summary>
[Category("AwsEvaluationSuite")]
public sealed class RedriveAllowPolicyDenyallBlocksAutomaticRedriveOnAlreadyWiredSource
{
    private const int OverReceives = 4;
    private const int VisibilityS = 1;

    private static Dictionary<string, string> Tags() => new() { ["sim2real:managed"] = "true", ["sim2real"] = "rap-denyall-blocks-auto-redrive" };

    /// <summary>
    /// RedriveAllowPolicy=denyAll on a DLQ stops the automatic maxReceiveCount redirect from an
    /// already-wired source: the message keeps being redelivered past the count and never lands in the
    /// DLQ; restoring allowAll lets one more over-count receive redirect it.
    /// </summary>
    [Test]
    [DisplayName("sqs.redrive-allow-policy-denyall-blocks-automatic-redrive-on-already-wired-source")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var token = Guid.NewGuid().ToString("N")[..8];

        var dlqUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-dlq-"), Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(dlqUrl));
        var dlqArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = dlqUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

        var srcUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-src-"), Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(srcUrl));
        var srcArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = srcUrl, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];

        var allow = new JsonObject { ["redrivePermission"] = "byQueue", ["sourceQueueArns"] = new JsonArray(srcArn) };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = dlqUrl, Attributes = new() { ["RedriveAllowPolicy"] = allow.ToJsonString() } });
        var redrive = new JsonObject { ["deadLetterTargetArn"] = dlqArn, ["maxReceiveCount"] = 1 };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = srcUrl, Attributes = new() { ["RedrivePolicy"] = redrive.ToJsonString() } });
        await AwaitAttribute(sqs, dlqUrl, "RedriveAllowPolicy", allow);
        await AwaitAttribute(sqs, srcUrl, "RedrivePolicy", redrive);

        // Revoke the allow policy BEFORE this message is ever received, so the whole lifecycle of
        // its over-count redelivery happens while denyAll is in effect.
        var deny = new JsonObject { ["redrivePermission"] = "denyAll" };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = dlqUrl, Attributes = new() { ["RedriveAllowPolicy"] = deny.ToJsonString() } });
        await AwaitAttribute(sqs, dlqUrl, "RedriveAllowPolicy", deny);

        var body = $"{token}-stuck";
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = srcUrl, MessageBody = body });

        // Each long poll outlasts the 1s visibility timeout, so it returns as soon as the message is
        // visible again rather than after a fixed sleep.
        for (var i = 0; i < OverReceives; i++)
        {
            var r = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = srcUrl,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = VisibilityS + 4,
                VisibilityTimeout = VisibilityS,
            });
            var msgs = r.Messages ?? [];
            Ensure(msgs.Count == 1 && msgs[0].Body == body,
                $"expected '{body}' still delivered from the SOURCE (never redirected), got [{string.Join(", ", msgs.Select(m => m.Body))}]");
        }

        var dlqPeek = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = dlqUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 2 })).Messages ?? [];
        Ensure(dlqPeek.Count == 0,
            $"RedriveAllowPolicy=denyAll must block the automatic redirect even {OverReceives}x "
            + $"past maxReceiveCount=1, but a direct receive from the DLQ found [{string.Join(", ", dlqPeek.Select(m => m.Body))}]");

        // Positive control: restore the allow policy and force one more over-count receive -- the
        // identical message must now redirect, proving denyAll (not a dead RedrivePolicy) was why
        // it never did before.
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = dlqUrl, Attributes = new() { ["RedriveAllowPolicy"] = allow.ToJsonString() } });
        await AwaitAttribute(sqs, dlqUrl, "RedriveAllowPolicy", allow);

        var moved = false;
        for (var i = 0; i < 8; i++)
        {
            var r = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = srcUrl,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = VisibilityS + 4,
                VisibilityTimeout = VisibilityS,
            });
            if (r.Messages is null or { Count: 0 })
            {
                moved = true;
                break;
            }
        }

        Ensure(moved, $"after restoring the allow policy, '{body}' should have redirected to the DLQ");
        var finalDlq = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = dlqUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 5 })).Messages ?? [];
        Ensure(finalDlq.Count == 1 && finalDlq[0].Body == body,
            $"positive control failed: expected '{body}' in the DLQ once the allow policy was "
            + $"restored, got [{string.Join(", ", finalDlq.Select(m => m.Body))}]");
    }

    /// <summary>Poll GetQueueAttributes until <paramref name="name"/> reads back as the JSON document <paramref name="expected"/>.</summary>
    private static async Task AwaitAttribute(IAmazonSQS sqs, string url, string name, JsonNode expected, double timeoutS = 30.0)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var raw = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = [name] })).Attributes!.GetValueOrDefault(name);
            if (raw is not null && JsonNode.DeepEquals(JsonNode.Parse(raw), expected))
            {
                return;
            }

            Ensure(deadline.Elapsed < TimeSpan.FromSeconds(timeoutS),
                $"{name} on '{url}' never read back as {expected.ToJsonString()} within {timeoutS}s; last '{raw}'");
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }
}
