// Translated from behavioral/events__s3__sqs/check_xsvc_s3_1_input_transformer_renders_s3_uri_not_envelope.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using EventsTag = Amazon.EventBridge.Model.Tag;
using S3Tag = Amazon.S3.Model.Tag;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.xsvc-s3-1-input-transformer-renders-s3-uri-not-envelope</summary>
[Category("AwsEvaluationSuite")]
public sealed class XsvcS31InputTransformerRendersS3UriNotEnvelope
{
    private const string Check = "xsvc-s3-1-input-transformer-renders-s3-uri-not-envelope";

    private const double DeliveryBudgetS = 180.0;

    /// <summary>
    /// A quoted s3://&lt;b&gt;/&lt;k&gt; InputTemplate makes an S3-sourced rule deliver the rendered JSON
    /// string of that URI to SQS, not the envelope, with the bucket and key the plain rule on the same
    /// bucket received.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.xsvc-s3-1-input-transformer-renders-s3-uri-not-envelope")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var (ev, s3, sqs) = (ctx.Events, ctx.S3, ctx.Sqs);
        var account = ctx.Account;
        var bucket = await MakeBucket(ctx, s3);
        var (xformUrl, xformArn) = await MakeQueue(ctx, sqs, account);
        var (rawUrl, rawArn) = await MakeQueue(ctx, sqs, account);
        JsonObject Pattern() => new()
        {
            ["source"] = new JsonArray("aws.s3"),
            ["detail-type"] = new JsonArray("Object Created"),
            ["detail"] = new JsonObject { ["bucket"] = new JsonObject { ["name"] = new JsonArray(bucket) } },
        };
        await MakeRule(ctx, ev, Pattern(), new Target
        {
            Arn = xformArn,
            InputTransformer = new InputTransformer
            {
                InputPathsMap = new() { ["k"] = "$.detail.object.key", ["b"] = "$.detail.bucket.name" },
                InputTemplate = "\"s3://<b>/<k>\"",
            },
        });
        await MakeRule(ctx, ev, Pattern(), new Target { Arn = rawArn });

        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest { BucketName = bucket, EventBridgeConfiguration = new EventBridgeConfiguration() });
        var enabled = await s3.GetBucketNotificationAsync(bucket);
        Ensure(
            enabled.EventBridgeConfiguration is not null,
            "bucket must be wired to EventBridge; GetBucketNotificationConfiguration -> no EventBridgeConfiguration");

        var xformBox = new RawMailbox(sqs, xformUrl, "transformed-target queue");
        var rawBox = new RawMailbox(sqs, rawUrl, "plain-target queue (barrier/observable)");

        // Retried with a fresh key per attempt: a freshly created rule/target can silently drop its
        // first delivered event (PutRule/PutTargets return before the subscription path is fully
        // live), and the drop is per-TARGET, so a retry re-fires a brand new S3 event and requires
        // BOTH targets to confirm it before accepting the attempt.
        var clock = FidelityTime.StartNew();
        var triggerDeadline = DeliveryBudgetS;
        var attempt = 0;
        string? key = null;
        string? rawBody = null;
        string? xformBody = null;
        while (key is null)
        {
            attempt++;
            var candidate = $"reports/2026/summary-{attempt}.csv";
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = candidate,
                InputStream = new MemoryStream(Encoding.UTF8.GetBytes("sim2real-input-transformer-bytes")),
            });
            var remaining = triggerDeadline - clock.Elapsed.TotalSeconds;
            Ensure(
                remaining > 0,
                $"event for {Py.Repr(candidate)} never reached both targets after {attempt} attempts; {rawBox.Label} bodies {Py.Repr(rawBox.Bodies)}, {xformBox.Label} bodies {Py.Repr(xformBox.Bodies)}");
            var window = Math.Min(30.0, remaining);
            rawBody = await rawBox.Poll(b => (string?)JsonNode.Parse(b)?["detail"]?["object"]?["key"] == candidate, window);
            if (rawBody is null)
            {
                continue;
            }

            xformBody = await xformBox.Poll(b => b == $"\"s3://{bucket}/{candidate}\"", window);
            if (xformBody is null)
            {
                continue;
            }

            key = candidate;
        }

        // --- barrier: the plain rule's full envelope is the independent observable the ------------
        // expected URI is built from, not a value this check assumed in advance.
        var envelope = JsonNode.Parse(rawBody!)!;
        Ensure(
            (string?)envelope["detail-type"] == "Object Created",
            $"barrier rule delivered {Py.Repr((string?)envelope["detail-type"])}, not 'Object Created'");
        var observedKey = (string?)envelope["detail"]?["object"]?["key"];
        var observedBucket = (string?)envelope["detail"]?["bucket"]?["name"];
        Ensure(
            observedKey == key && observedBucket == bucket,
            $"barrier envelope must name the object just put; got bucket={Py.Repr(observedBucket)} key={Py.Repr(observedKey)}, expected bucket={Py.Repr(bucket)} key={Py.Repr(key)}");

        // --- the transformed body ------------------------------------------------------------------
        var bareUri = $"s3://{observedBucket}/{observedKey}";
        var expectedLiteral = $"\"{bareUri}\"";
        Ensure(
            xformBody == expectedLiteral,
            $"InputTransformer must deliver exactly {Py.Repr(expectedLiteral)}; got {Py.Repr(xformBody)}");
        var parsed = JsonNode.Parse(xformBody!);
        Ensure(
            parsed is JsonValue v && v.TryGetValue<string>(out var parsedString) && parsedString == bareUri,
            $"the delivered body must round-trip through JSON to the bare URI {Py.Repr(bareUri)}; {Py.Repr(xformBody)} parsed as {parsed?.ToJsonString()}");
        Ensure(
            !xformBody!.Contains("detail-type", StringComparison.Ordinal) && !xformBody.Contains("source", StringComparison.Ordinal),
            $"the transformed body must not carry envelope fields; got {Py.Repr(xformBody)}");
    }

    /// <summary>
    /// Keeps every raw message BODY, never JSON-decoded: the claim under test is that the transformed
    /// body is a bare string, so decoding it here would hide the very defect (an emulator that
    /// delivers the JSON envelope instead) this check exists to catch.
    /// </summary>
    private sealed class RawMailbox(AmazonSQSClient sqs, string url, string label)
    {
        public string Label => label;

        public List<string> Bodies { get; } = [];

        private async Task Pull(int wait)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = wait });
            var msgs = resp.Messages ?? [];
            if (msgs.Count > 0)
            {
                await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
                {
                    QueueUrl = url,
                    Entries = [.. msgs.Select((m, i) => new DeleteMessageBatchRequestEntry { Id = $"d{i}", ReceiptHandle = m.ReceiptHandle })],
                });
            }

            Bodies.AddRange(msgs.Select(m => m.Body));
        }

        /// <summary>The first body matching predicate, or None if budget_s runs out first.</summary>
        public async Task<string?> Poll(Func<string, bool> predicate, double budgetS)
        {
            var clock = FidelityTime.StartNew();
            while (true)
            {
                var hits = Bodies.Where(predicate).ToList();
                if (hits.Count > 0)
                {
                    return hits[0];
                }

                var remaining = budgetS - clock.Elapsed.TotalSeconds;
                if (remaining <= 0)
                {
                    return null;
                }

                await Pull(Math.Max(1, Math.Min(5, (int)remaining)));
            }
        }
    }

    private static async Task<string> MakeBucket(FidelityContext ctx, AmazonS3Client s3)
    {
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-xsvc-s3t-", s3);
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = bucket, TagSet = [new S3Tag { Key = "sim2real", Value = Check }] });
        return bucket;
    }

    /// <summary>A queue whose policy admits default-bus rules; S3 service events land on the DEFAULT bus.</summary>
    private static async Task<(string Url, string Arn)> MakeQueue(FidelityContext ctx, AmazonSQSClient sqs, string account)
    {
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-xsvc-s3t-"),
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "AllowEventBridge",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn,
                ["Condition"] = new JsonObject { ["ArnLike"] = new JsonObject { ["aws:SourceArn"] = $"arn:aws:events:{ctx.Region}:{account}:rule/*" } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (url, arn);
    }

    private static async Task<string> MakeRule(FidelityContext ctx, AmazonEventBridgeClient ev, JsonObject pattern, Target target)
    {
        var rule = FidelityContext.UniqueName("sim2real-xsvc-s3t-");
        await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = rule,
            State = RuleState.ENABLED,
            EventPattern = pattern.ToJsonString(),
            Tags = [new EventsTag { Key = "sim2real", Value = Check }],
        });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule }));
        target.Id = "q";
        var resp = await ev.PutTargetsAsync(new PutTargetsRequest { Rule = rule, Targets = [target] });
        ctx.OnCleanup(() => ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, Ids = ["q"] }));
        Ensure(
            resp.FailedEntryCount == 0,
            $"put_targets on {Py.Repr(rule)} reported failures: [{string.Join(", ", (resp.FailedEntries ?? []).Select(f => $"{f.TargetId}: {f.ErrorCode} {f.ErrorMessage}"))}]");
        return rule;
    }
}
