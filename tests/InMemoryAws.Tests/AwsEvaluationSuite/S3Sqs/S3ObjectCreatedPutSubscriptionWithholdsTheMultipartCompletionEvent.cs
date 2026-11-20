// Translated from behavioral/s3__sqs/check_s3_object_created_put_subscription_withholds_the_multipart_completion_event.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3Sqs;

/// <summary>s3__sqs.s3-object-created-put-subscription-withholds-the-multipart-completion-event</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3ObjectCreatedPutSubscriptionWithholdsTheMultipartCompletionEvent
{
    private const string Check = "s3-object-created-put-subscription-withholds-the-multipart-completion-event";
    private const string Prefix = "mpu/";
    private const int NParts = 2;
    private const double HoldCapS = 180.0;
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    private static readonly byte[] Part = [.. Enumerable.Repeat((byte)'M', 5 * 1024 * 1024)];

    /// <summary>
    /// Two queues on one prefix subscribed to ObjectCreated:Put and CompleteMultipartUpload each get
    /// only their event: the multipart completion (with the assembled size and ETag) reaches only its
    /// queue, while the Put queue stays empty for max(3L, 15s) and still delivers a later PutObject.
    /// </summary>
    [Test]
    [DisplayName("s3__sqs.s3-object-created-put-subscription-withholds-the-multipart-completion-event")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var sqs = ctx.Sqs;
        var (bucketName, bucketArn) = await CreateBucket(ctx);
        var qPut = await CreateQueue(ctx);
        var qMpu = await CreateQueue(ctx);

        foreach (var q in new[] { qPut, qMpu })
        {
            await SetQueuePolicy(ctx, q.Url, AllowServiceSend(q.Arn, "s3.amazonaws.com", bucketArn));
        }

        var prefixFilter = new Filter { S3KeyFilter = new S3KeyFilter { FilterRules = [new FilterRule("prefix", Prefix)] } };
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucketName,
            QueueConfigurations =
            [
                new QueueConfiguration { Id = "put-only", Queue = qPut.Arn, Events = [new EventType("s3:ObjectCreated:Put")], Filter = prefixFilter },
                new QueueConfiguration { Id = "mpu-only", Queue = qMpu.Arn, Events = [new EventType("s3:ObjectCreated:CompleteMultipartUpload")], Filter = prefixFilter },
            ],
        });
        var got = await s3.GetBucketNotificationAsync(bucketName);
        var events = (got.QueueConfigurations ?? []).ToDictionary(c => c.Id, c => string.Join(",", (c.Events ?? []).Select(e => e.Value)));
        Ensure(
            events.Count == 2
                && events.GetValueOrDefault("put-only") == "s3:ObjectCreated:Put"
                && events.GetValueOrDefault("mpu-only") == "s3:ObjectCreated:CompleteMultipartUpload",
            $"GetBucketNotificationConfiguration reports {Py.Repr(events)}; expected the two disjoint " +
            "single-event subscriptions this check installed");

        // S3 validates each new destination by sending it one s3:TestEvent.
        foreach (var (label, q) in new[] { ("put-only", qPut), ("mpu-only", qMpu) })
        {
            var (testEvents, others) = await WaitFor(ctx, q.Url, b => b is JsonObject o && (string?)o["Event"] == "s3:TestEvent", timeoutS: 60.0);
            Ensure(
                testEvents.Count > 0,
                $"the {label} destination received no s3:TestEvent when the notification configuration " +
                $"was installed; it saw {Py.Repr(others.Take(2).Select(m => m.Body).ToList())}");
        }

        // Control: a plain PutObject reaches the Put-only queue, and L is its measured latency.
        var plainKey = $"{Prefix}plain.txt";
        var started = FidelityTime.StartNew();
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucketName, Key = plainKey, ContentBody = "plain-single-part-write" });
        var (matched, rest) = await WaitFor(ctx, qPut.Url, b => HasRecord(b, plainKey), timeoutS: 120.0);
        Ensure(
            matched.Count > 0,
            $"control: PutObject {Py.Repr(plainKey)} produced no notification on the s3:ObjectCreated:Put " +
            $"destination within 120s; non-matching traffic seen: {Py.Repr(rest.Take(2).Select(m => m.Body).ToList())}");
        var putLatencyS = started.Elapsed.TotalSeconds;
        var record = Record(Body(matched[0]), plainKey);
        Ensure(
            (string?)record["eventName"] == "ObjectCreated:Put",
            $"the PutObject notification carries eventName {Py.Repr((string?)record["eventName"])}, expected 'ObjectCreated:Put'");

        var mpuKey = $"{Prefix}assembled.bin";
        var upload = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucketName, Key = mpuKey });
        var parts = new List<PartETag>();
        for (var number = 1; number <= NParts; number++)
        {
            var uploaded = await s3.UploadPartAsync(new UploadPartRequest
            {
                BucketName = bucketName,
                Key = mpuKey,
                UploadId = upload.UploadId,
                PartNumber = number,
                InputStream = new MemoryStream(Part),
            });
            parts.Add(new PartETag { PartNumber = number, ETag = uploaded.ETag });
        }

        var mpuStarted = FidelityTime.StartNew();
        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucketName,
            Key = mpuKey,
            UploadId = upload.UploadId,
            PartETags = parts,
        });
        var head = await s3.GetObjectMetadataAsync(bucketName, mpuKey);
        var expectedEtag = head.ETag.Trim('"');
        var expectedSize = head.ContentLength;
        Ensure(expectedSize == NParts * Part.Length, $"the assembled object is {expectedSize} bytes, expected {NParts * Part.Length}");

        (matched, rest) = await WaitFor(ctx, qMpu.Url, b => HasRecord(b, mpuKey), timeoutS: 120.0);
        Ensure(
            matched.Count > 0,
            $"CompleteMultipartUpload on {Py.Repr(mpuKey)} produced no notification on the " +
            "s3:ObjectCreated:CompleteMultipartUpload destination within 120s; traffic seen: " +
            $"{Py.Repr(rest.Take(2).Select(m => m.Body).ToList())}");
        var mpuLatencyS = mpuStarted.Elapsed.TotalSeconds;
        record = Record(Body(matched[0]), mpuKey);
        Ensure(
            (string?)record["eventName"] == "ObjectCreated:CompleteMultipartUpload",
            $"the multipart completion notification carries eventName {Py.Repr((string?)record["eventName"])}, expected " +
            "'ObjectCreated:CompleteMultipartUpload' — the four ObjectCreated sub-types are being " +
            "collapsed into one");
        var size = record["s3"]!["object"]!["size"];
        Ensure(
            size is JsonValue sv && sv.TryGetValue<long>(out var n) && n == expectedSize,
            $"the notification reports size {size?.ToJsonString() ?? "None"}; HeadObject reports " +
            $"{expectedSize} for the same object");
        var etag = (string?)record["s3"]!["object"]!["eTag"];
        Ensure(
            etag == expectedEtag,
            $"the notification reports eTag {Py.Repr(etag)}; HeadObject reports " +
            $"{Py.Repr(expectedEtag)} (part-count-suffixed, unquoted) for the same object");

        var holdS = Math.Min(Math.Max(3.0 * Math.Max(putLatencyS, mpuLatencyS), 15.0), HoldCapS);
        var leaked = await Collect(ctx, qPut.Url, windowS: holdS);
        var leakedKeys = leaked
            .Select(Body)
            .OfType<JsonObject>()
            .SelectMany(b => b["Records"]?.AsArray().OfType<JsonObject>() ?? [])
            .Select(r => (string?)r["s3"]?["object"]?["key"])
            .ToList();
        Ensure(
            leakedKeys.Count == 0,
            $"the s3:ObjectCreated:Put destination received notification(s) for {Py.Repr(leakedKeys)} within " +
            $"{holdS:F0}s — a multipart completion is not an ObjectCreated:Put, and the multipart " +
            $"event itself arrived on the other queue in {mpuLatencyS:F1}s, so a leaked copy would " +
            "have arrived by now");
        // second observable: the Put queue's visible counter polls to 0 after the empty long-poll
        await PollQueueCounters(ctx, sqs, qPut.Url, new() { ["ApproximateNumberOfMessages"] = "0" });

        // Re-prove delivery, so the empty window cannot mean the destination went quiet.
        var secondKey = $"{Prefix}plain-again.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucketName, Key = secondKey, ContentBody = "second-single-part-write" });
        (matched, _) = await WaitFor(ctx, qPut.Url, b => HasRecord(b, secondKey), timeoutS: 120.0);
        Ensure(
            matched.Count > 0,
            $"a second PutObject {Py.Repr(secondKey)} produced no notification on the Put destination — " +
            "without this the empty window above would only mean the destination had gone quiet");
        Ensure(
            (string?)Record(Body(matched[0]), secondKey)["eventName"] == "ObjectCreated:Put",
            "the second PutObject's notification does not carry eventName 'ObjectCreated:Put'");
    }

    private sealed record Queue(string Name, string Url, string Arn);

    private static Dictionary<string, string> XsvcResourceTags() => new() { [ManagedTagKey] = "true", ["sim2real:check"] = Check };

    /// <summary>Create a tagged SQS queue (registers delete_queue teardown). Returns {name,url,arn}.</summary>
    private static async Task<Queue> CreateQueue(FidelityContext ctx)
    {
        var name = FidelityContext.UniqueName($"{XsvcPrefix}q-");
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = XsvcResourceTags() })).QueueUrl;
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        return new Queue(name, url, arn);
    }

    /// <summary>Parse an SQS message body as JSON, falling back to the raw string.</summary>
    private static JsonNode? Body(SqsMessage msg)
    {
        var raw = msg.Body ?? string.Empty;
        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return JsonValue.Create(raw);
        }
    }

    /// <summary>True when a parsed notification body carries a Records entry for <paramref name="key"/>.</summary>
    private static bool HasRecord(JsonNode? body, string key) =>
        body is JsonObject o && (o["Records"]?.AsArray().OfType<JsonObject>().Any(r => (string?)r["s3"]?["object"]?["key"] == key) ?? false);

    /// <summary>The single Records entry for <paramref name="key"/>. Hard-fails rather than defaulting.</summary>
    private static JsonObject Record(JsonNode? body, string key)
    {
        var matches = body?["Records"]?.AsArray().OfType<JsonObject>().Where(r => (string?)r["s3"]?["object"]?["key"] == key).ToList() ?? [];
        Ensure(
            matches.Count == 1,
            $"expected exactly one notification record for {Py.Repr(key)}, found {matches.Count} in {body?.ToJsonString()}");
        return matches[0];
    }

    /// <summary>A poll's timeout: cap_s, bounded by the runner's remaining time less 5 s.</summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>
    /// Poll GetQueueAttributes until every named counter equals its expected string value. The
    /// ApproximateNumberOf* counters may lag up to a minute after producers stop.
    /// </summary>
    private static async Task PollQueueCounters(FidelityContext ctx, AmazonSQSClient sqs, string queueUrl, Dictionary<string, string> expected, double capS = 90.0)
    {
        var budgetS = BudgetS(ctx, capS);
        var start = FidelityTime.StartNew();
        var stop = Math.Min(capS, budgetS);
        while (true)
        {
            var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = [.. expected.Keys] })).Attributes;
            var observed = expected.Keys.ToDictionary(name => name, name => attrs.GetValueOrDefault(name));
            if (observed.All(o => o.Value == expected[o.Key]))
            {
                return;
            }

            if (start.Elapsed.TotalSeconds >= stop)
            {
                if (budgetS < capS)
                {
                    throw new TimeoutException(
                        $"the check's time budget ran out after {start.Elapsed.TotalSeconds:F1}s, before the " +
                        $"{capS}s counter wait ended (last observed {Py.Repr(observed)}); not graded");
                }

                throw Fail($"queue counters never reached {Py.Repr(expected)}; last observed {Py.Repr(observed)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>Drain a queue over a bounded window with long-polling receives; return every message seen.</summary>
    private static async Task<List<SqsMessage>> Collect(FidelityContext ctx, string queueUrl, double windowS = 30.0, int maxTotal = 20)
    {
        var sqs = ctx.Sqs;
        var seen = new List<SqsMessage>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed.TotalSeconds < windowS && seen.Count < maxTotal)
        {
            var wait = (int)Math.Min(5, Math.Max(1, windowS - started.Elapsed.TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = wait });
            foreach (var msg in resp.Messages ?? [])
            {
                seen.Add(msg);
                await sqs.DeleteMessageAsync(queueUrl, msg.ReceiptHandle);
            }
        }

        return seen;
    }

    /// <summary>Poll a queue until a message whose parsed body satisfies predicate() arrives. Returns (matched, others).</summary>
    private static async Task<(List<SqsMessage> Matched, List<SqsMessage> Others)> WaitFor(
        FidelityContext ctx, string queueUrl, Func<JsonNode?, bool> predicate, double timeoutS = 60.0)
    {
        var matched = new List<SqsMessage>();
        var others = new List<SqsMessage>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed.TotalSeconds < timeoutS && matched.Count == 0)
        {
            foreach (var msg in await Collect(ctx, queueUrl, windowS: Math.Min(2.0, timeoutS)))
            {
                (predicate(Body(msg)) ? matched : others).Add(msg);
            }
        }

        return (matched, others);
    }

    /// <summary>A queue policy granting one AWS service SendMessage on this queue, scoped by SourceArn.</summary>
    private static JsonObject AllowServiceSend(string queueArn, string service, string sourceArn) => new()
    {
        ["Version"] = "2012-10-17",
        ["Statement"] = new JsonArray(new JsonObject
        {
            ["Sid"] = "AllowServiceSend",
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["Service"] = service },
            ["Action"] = "sqs:SendMessage",
            ["Resource"] = queueArn,
            ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = sourceArn } },
        }),
    };

    private static Task SetQueuePolicy(FidelityContext ctx, string queueUrl, JsonObject policy) =>
        ctx.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queueUrl, Attributes = new() { ["Policy"] = policy.ToJsonString() } });

    /// <summary>Create a tagged S3 bucket (registers teardown that aborts uploads and empties it). Returns {name,arn}.</summary>
    private static async Task<(string Name, string Arn)> CreateBucket(FidelityContext ctx)
    {
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, $"{XsvcPrefix}bkt-", s3);
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = name,
            TagSet = [.. XsvcResourceTags().Select(t => new Tag { Key = t.Key, Value = t.Value })],
        });
        return (name, $"arn:aws:s3:::{name}");
    }
}
