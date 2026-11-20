using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using EventBridgeTag = Amazon.EventBridge.Model.Tag;
using S3Tag = Amazon.S3.Model.Tag;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>
/// The <c>_filter_scenario</c> the <c>xsvc-s3-1-*-filter-*</c> checks share: one bucket sending its
/// events to EventBridge, a rule with a content filter on <c>detail.object</c> and an unfiltered
/// observer rule, a warm-up, near-miss keys put before matching controls, and an absence window
/// calibrated on the measured delivery latency.
/// </summary>
internal static class XsvcS3FilterScenario
{
    public const double DeliveryBudgetS = 180.0;
    public const double AbsenceFloorBudgetS = 30.0;
    public const double AbsenceCeilingBudgetS = 90.0;

    /// <summary>How the upstream file grades the outcome where the four files differ.</summary>
    /// <param name="MaxCredibleDeliveryWaitS">The slowest positive delivery an absence window is calibrated against.</param>
    /// <param name="TolerateUnconfirmedWarmups">
    /// Whether warm-up attempts other than the confirmed one may be absent from the final sets
    /// (the size check), rather than required in them (the key checks).
    /// </param>
    /// <param name="WaitForZeroCounters">Whether the final depth is read until its counters are "0" (the size check).</param>
    public sealed record Grading(double MaxCredibleDeliveryWaitS, bool TolerateUnconfirmedWarmups, bool WaitForZeroCounters);

    public static async Task Run(
        FidelityContext ctx,
        string check,
        JsonObject objectFilter,
        Func<int, (string Key, int Size)> warmupKeyFn,
        List<(string Key, int Size)> nearMisses,
        List<(string Key, int Size)> controls,
        Grading grading)
    {
        var (ev, s3, sqs) = (ctx.Events, ctx.S3, ctx.Sqs);
        var account = ctx.Account;

        var bucket = await MakeBucket(ctx, s3, check);
        var (filtUrl, filtArn) = await MakeQueue(ctx, sqs, account, check);
        var (obsUrl, obsArn) = await MakeQueue(ctx, sqs, account, check);

        JsonObject Base() => new()
        {
            ["source"] = new JsonArray("aws.s3"),
            ["detail-type"] = new JsonArray("Object Created"),
            ["detail"] = new JsonObject { ["bucket"] = new JsonObject { ["name"] = new JsonArray(bucket) } },
        };
        var filtered = Base();
        filtered["detail"]!["object"] = objectFilter.DeepClone();
        await MakeRule(ctx, ev, filtered, filtArn, check);
        await MakeRule(ctx, ev, Base(), obsArn, check);

        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest { BucketName = bucket, EventBridgeConfiguration = new EventBridgeConfiguration() });
        var enabled = await s3.GetBucketNotificationAsync(bucket);
        Ensure(enabled.EventBridgeConfiguration is not null, "bucket must be wired to EventBridge; GetBucketNotificationConfiguration -> no EventBridgeConfiguration");

        var filt = new Mailbox(sqs, filtUrl, "filtered-rule queue");
        var obs = new Mailbox(sqs, obsUrl, "unfiltered observer queue");

        Task Put(string key, int size) =>
            s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(Enumerable.Repeat((byte)'x', size).ToArray()) });

        // --- warm-up: one matching object must reach BOTH rules before the trial starts ----------
        // Retried with a fresh key per attempt: a freshly created rule/target can silently drop its
        // first delivered event, and EventBridge never retries a dropped one.
        var warm = FidelityTime.StartNew();
        var attempt = 0;
        string? warmupKey = null;
        double warmLatency = 0;
        // Every attempt is tracked, not just the one that is confirmed first: an earlier attempt
        // whose event was merely SLOW (not truly dropped) can still land later.
        var attemptedWarmups = new List<(string Key, int Size)>();
        while (warmupKey is null)
        {
            attempt++;
            var (key, size) = warmupKeyFn(attempt);
            attemptedWarmups.Add((key, size));
            await Put(key, size);
            var remaining = DeliveryBudgetS - warm.Elapsed.TotalSeconds;
            Ensure(remaining > 0, $"warm-up key never delivered after {attempt} attempts");
            var window = Math.Min(30.0, remaining);
            var filtLat = await filt.PollKeys([key], window);
            var obsLat = filtLat is not null ? await obs.PollKeys([key], window) : null;
            if (obsLat is null)
            {
                continue;
            }

            (warmupKey, warmLatency) = (key, Math.Max(filtLat!.Value, obsLat.Value));
        }

        // --- trial: near-misses first, then the matching control, same bucket, same rule ----------
        foreach (var (key, size) in nearMisses)
        {
            await Put(key, size);
        }

        foreach (var (key, size) in controls)
        {
            await Put(key, size);
        }

        var controlKeys = controls.Select(c => c.Key).ToHashSet();
        var missKeys = nearMisses.Select(c => c.Key).ToHashSet();
        var controlSizes = controls.ToDictionary(c => c.Key, c => c.Size);
        var missSizes = nearMisses.ToDictionary(c => c.Key, c => c.Size);
        // Retried: a timed-out wait re-puts whichever keys are still missing -- an idempotent
        // overwrite that fires a fresh S3 event -- rather than trusting a single wait.
        var trial = FidelityTime.StartNew();
        double? trialLatency = null;
        while (trialLatency is null)
        {
            var remaining = DeliveryBudgetS - trial.Elapsed.TotalSeconds;
            Ensure(remaining > 0, $"control keys {Py.Repr(controlKeys.Except(filt.Keys()).Order(StringComparer.Ordinal).ToList())} never delivered after retries");
            trialLatency = await filt.PollKeys(controlKeys, Math.Min(60.0, remaining));
            if (trialLatency is null)
            {
                foreach (var k in controlKeys.Except(filt.Keys()).Order(StringComparer.Ordinal))
                {
                    await Put(k, controlSizes[k]);
                }
            }
        }

        // The observer has no content filter, so it must see every near-miss: that proves S3
        // EMITTED each one and the bus delivered it over identical wiring. Retried the same way.
        var obsWait = FidelityTime.StartNew();
        var allKeys = missKeys.Union(controlKeys).ToHashSet();
        var obsDone = false;
        while (!obsDone)
        {
            var remaining = DeliveryBudgetS - obsWait.Elapsed.TotalSeconds;
            Ensure(remaining > 0, $"observer missing {Py.Repr(allKeys.Except(obs.Keys()).Order(StringComparer.Ordinal).ToList())} after retries");
            obsDone = await obs.PollKeys(allKeys, Math.Min(60.0, remaining)) is not null;
            if (!obsDone)
            {
                foreach (var k in allKeys.Except(obs.Keys()).Order(StringComparer.Ordinal))
                {
                    await Put(k, controlSizes.TryGetValue(k, out var s) ? s : missSizes[k]);
                }
            }
        }

        var measured = Math.Max(warmLatency, trialLatency.Value);
        Ensure(
            measured <= grading.MaxCredibleDeliveryWaitS,
            $"slowest positive delivery took {measured:F1}s, above the {grading.MaxCredibleDeliveryWaitS}s this check will calibrate an absence against");
        var soakWindow = Math.Min(Math.Max(AbsenceFloorBudgetS, 3.0 * measured), AbsenceCeilingBudgetS);
        await filt.Soak(soakWindow);

        var leaked = filt.Keys().Intersect(missKeys).Order(StringComparer.Ordinal).ToList();
        Ensure(
            leaked.Count == 0,
            $"filter {objectFilter.ToJsonString()} must withhold the near-miss keys, but the filtered queue " +
            $"received {Py.Repr(leaked)} within {soakWindow:F0}s (the observer received all of " +
            $"{Py.Repr(missKeys.Order(StringComparer.Ordinal).ToList())}, and the same rule delivered the control {Py.Repr(controlKeys.Order(StringComparer.Ordinal).ToList())})");
        if (grading.TolerateUnconfirmedWarmups)
        {
            var unconfirmed = attemptedWarmups.Select(w => w.Key).Where(k => k != warmupKey).ToHashSet();
            var expected = controlKeys.Append(warmupKey).ToHashSet();
            Ensure(
                filt.Keys().Except(unconfirmed).ToHashSet().SetEquals(expected),
                $"filtered queue must hold exactly the matching keys {Py.Repr(expected.Order(StringComparer.Ordinal).ToList())} (plus any of " +
                $"the unconfirmed warm-ups {Py.Repr(unconfirmed.Order(StringComparer.Ordinal).ToList())}); got {Py.Repr(filt.Keys().Order(StringComparer.Ordinal).ToList())}");
            Ensure(
                obs.Keys().Except(unconfirmed).ToHashSet().SetEquals(expected.Union(missKeys)),
                $"observer must hold every key put; expected {Py.Repr(expected.Union(missKeys).Order(StringComparer.Ordinal).ToList())}, " +
                $"got {Py.Repr(obs.Keys().Order(StringComparer.Ordinal).ToList())}");
        }
        else
        {
            var expected = controlKeys.Union(attemptedWarmups.Select(w => w.Key)).ToHashSet();
            Ensure(
                filt.Keys().SetEquals(expected),
                $"filtered queue must hold exactly the matching keys {Py.Repr(expected.Order(StringComparer.Ordinal).ToList())}; " +
                $"got {Py.Repr(filt.Keys().Order(StringComparer.Ordinal).ToList())}");
            Ensure(
                obs.Keys().SetEquals(expected.Union(missKeys)),
                $"observer must hold every key put; expected {Py.Repr(expected.Union(missKeys).Order(StringComparer.Ordinal).ToList())}, " +
                $"got {Py.Repr(obs.Keys().Order(StringComparer.Ordinal).ToList())}");
        }

        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (k, s) in attemptedWarmups.Concat(nearMisses).Concat(controls))
        {
            sizes[k] = s;
        }

        foreach (var env in filt.Envelopes())
        {
            var key = (string)env["detail"]!["object"]!["key"]!;
            Ensure((string?)env["source"] == "aws.s3", $"source must be 'aws.s3', got {Py.Repr((string?)env["source"])}");
            Ensure((string?)env["detail-type"] == "Object Created", $"detail-type must be 'Object Created', got {Py.Repr((string?)env["detail-type"])}");
            Ensure(
                (string?)env["detail"]!["bucket"]!["name"] == bucket,
                $"detail.bucket.name must be {Py.Repr(bucket)}, got {Py.Repr((string?)env["detail"]!["bucket"]!["name"])}");
            Ensure(
                (long?)env["detail"]!["object"]!["size"] == sizes[key],
                $"detail.object.size for {Py.Repr(key)} must be {sizes[key]}, got {env["detail"]!["object"]!["size"]?.ToJsonString()}");
        }

        var depth = grading.WaitForZeroCounters ? await filt.DepthWhenZero(ctx) : await filt.Depth();
        Ensure(
            depth.Count == 2 && depth.GetValueOrDefault("ApproximateNumberOfMessages") == "0" && depth.GetValueOrDefault("ApproximateNumberOfMessagesNotVisible") == "0",
            $"filtered queue must be empty after the soak; attributes {Py.Repr(depth)}");
    }

    private static async Task EmptyAndDeleteBucket(IAmazonS3 s3, string bucket)
    {
        var versions = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket });
        var items = (versions.Versions ?? []).Select(v => new KeyVersion { Key = v.Key, VersionId = v.VersionId }).ToList();
        if (items.Count > 0)
        {
            var resp = await s3.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = bucket, Objects = items, Quiet = true });
            if (resp.DeleteErrors is { Count: > 0 } failed)
            {
                throw new InvalidOperationException($"DeleteObjects left {failed.Count} object(s)");
            }
        }

        await s3.DeleteBucketAsync(bucket);
    }

    private static async Task<string> MakeBucket(FidelityContext ctx, AmazonS3Client s3, string check)
    {
        var bucket = FidelityContext.UniqueName("sim2real-xsvc-s3f-");
        var request = new PutBucketRequest { BucketName = bucket, UseClientRegion = false };
        if (ctx.Region != "us-east-1")
        {
            request.PutBucketConfiguration = new PutBucketConfiguration { LocationConstraint = new BucketLocationConstraint(ctx.Region) };
        }

        await s3.PutBucketAsync(request);
        ctx.OnCleanup(() => EmptyAndDeleteBucket(s3, bucket));
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = bucket, TagSet = [new S3Tag { Key = "sim2real", Value = check }] });
        return bucket;
    }

    /// <summary>A queue whose policy admits default-bus rules; S3 service events land on the DEFAULT bus.</summary>
    private static async Task<(string Url, string Arn)> MakeQueue(FidelityContext ctx, AmazonSQSClient sqs, string account, string check)
    {
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-xsvc-s3f-"),
            Tags = new() { ["sim2real"] = check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new()
            {
                ["Policy"] = new JsonObject
                {
                    ["Version"] = "2012-10-17",
                    ["Statement"] = new JsonArray(new JsonObject
                    {
                        ["Sid"] = "AllowEventBridge",
                        ["Effect"] = "Allow",
                        ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
                        ["Action"] = "sqs:SendMessage",
                        ["Resource"] = arn,
                        ["Condition"] = new JsonObject
                        {
                            ["ArnLike"] = new JsonObject { ["aws:SourceArn"] = $"arn:aws:events:{ctx.Region}:{account}:rule/*" },
                        },
                    }),
                }.ToJsonString(),
            },
        });
        return (url, arn);
    }

    private static async Task<string> MakeRule(FidelityContext ctx, AmazonEventBridgeClient ev, JsonObject pattern, string queueArn, string check)
    {
        var rule = FidelityContext.UniqueName("sim2real-xsvc-s3f-");
        await ev.PutRuleAsync(new PutRuleRequest
        {
            Name = rule,
            State = RuleState.ENABLED,
            EventPattern = pattern.ToJsonString(),
            Tags = [new EventBridgeTag { Key = "sim2real", Value = check }],
        });
        ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule }));
        var resp = await ev.PutTargetsAsync(new PutTargetsRequest { Rule = rule, Targets = [new Target { Id = "q", Arn = queueArn }] });
        ctx.OnCleanup(() => ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, Ids = ["q"] }));
        Ensure(resp.FailedEntryCount == 0, $"put_targets on {Py.Repr(rule)} reported failures: {Py.Repr(resp.FailedEntries?.Select(f => f.ErrorMessage).ToList())}");
        var stored = JsonNode.Parse((await ev.DescribeRuleAsync(new DescribeRuleRequest { Name = rule })).EventPattern);
        Ensure(
            JsonNode.DeepEquals(stored, pattern),
            $"DescribeRule must return the pattern as written; wrote {pattern.ToJsonString()}, got {stored?.ToJsonString()}");
        return rule;
    }

    /// <summary>The mailbox keeps EVERY body it drains so that waiting for one key never discards another key's evidence.</summary>
    private sealed class Mailbox(AmazonSQSClient sqs, string url, string label)
    {
        private readonly List<string> _bodies = [];

        public string Label => label;

        private async Task Pull(int wait)
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = wait })).Messages ?? [];
            if (msgs.Count > 0)
            {
                await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
                {
                    QueueUrl = url,
                    Entries = [.. msgs.Select((m, i) => new DeleteMessageBatchRequestEntry { Id = $"d{i}", ReceiptHandle = m.ReceiptHandle })],
                });
            }

            _bodies.AddRange(msgs.Select(m => m.Body));
        }

        public List<JsonObject> Envelopes() => [.. _bodies.Select(b => JsonNode.Parse(b)!.AsObject())];

        public HashSet<string> Keys() => Envelopes().Select(e => (string)e["detail"]!["object"]!["key"]!).ToHashSet(StringComparer.Ordinal);

        /// <summary>Seconds until every wanted key has arrived, or null if <paramref name="budgetS"/> runs out first.</summary>
        public async Task<double?> PollKeys(IReadOnlyCollection<string> wanted, double budgetS)
        {
            var started = FidelityTime.StartNew();
            while (!wanted.All(Keys().Contains))
            {
                var remaining = budgetS - started.Elapsed.TotalSeconds;
                if (remaining <= 0)
                {
                    return null;
                }

                await Pull(Math.Max(1, Math.Min(5, (int)remaining)));
            }

            return started.Elapsed.TotalSeconds;
        }

        public async Task Soak(double budgetS)
        {
            var started = FidelityTime.StartNew();
            double remaining;
            while ((remaining = budgetS - started.Elapsed.TotalSeconds) > 0)
            {
                await Pull(Math.Max(1, Math.Min(5, (int)remaining)));
            }
        }

        public async Task<Dictionary<string, string>> Depth() =>
            (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
            })).Attributes;

        /// <summary>
        /// Read the queue's counters until each is "0" or the cap ends; return the last read. SQS
        /// documents these counters as eventually consistent.
        /// </summary>
        public async Task<Dictionary<string, string>> DepthWhenZero(FidelityContext ctx, double capS = 90.0, double intervalS = 3.0)
        {
            var started = FidelityTime.StartNew();
            var budgetS = Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));
            var stop = Math.Min(capS, budgetS);
            while (true)
            {
                var attrs = await Depth();
                if (attrs.Values.All(v => v == "0"))
                {
                    return attrs;
                }

                if (started.Elapsed.TotalSeconds >= stop)
                {
                    if (budgetS < capS)
                    {
                        throw new TimeoutException(
                            $"the check's time budget ran out after {started.Elapsed.TotalSeconds:F1}s, before the {capS}s counter wait ended; not graded");
                    }

                    return attrs;
                }

                await ctx.Delay(TimeSpan.FromSeconds(intervalS));
            }
        }
    }
}
