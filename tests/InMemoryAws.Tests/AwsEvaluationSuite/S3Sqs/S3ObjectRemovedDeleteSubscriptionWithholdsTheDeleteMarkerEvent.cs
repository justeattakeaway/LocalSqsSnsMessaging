// Translated from behavioral/s3__sqs/check_s3_object_removed_delete_subscription_withholds_the_delete_marker_event.py
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

/// <summary>s3__sqs.s3-object-removed-delete-subscription-withholds-the-delete-marker-event</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3ObjectRemovedDeleteSubscriptionWithholdsTheDeleteMarkerEvent
{
    private const string Check = "s3-object-removed-delete-subscription-withholds-the-delete-marker-event";
    private const string Prefix = "v/";
    private const string Key = Prefix + "subject.bin";
    private const string BodyText = "versioned-removal-payload";
    private const double HoldCapS = 180.0;
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// On a versioned bucket, a delete marker reaches only the ObjectRemoved:DeleteMarkerCreated queue
    /// and a permanent version delete only the ObjectRemoved:Delete queue, each carrying its versionId and
    /// no size; each queue stays quiet for the other's event, and ListObjectVersions agrees.
    /// </summary>
    [Test]
    [DisplayName("s3__sqs.s3-object-removed-delete-subscription-withholds-the-delete-marker-event")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var sqs = ctx.Sqs;
        var (bucketName, bucketArn) = await CreateVersionedBucket(ctx);
        var qDelete = await CreateQueue(ctx);
        var qMarker = await CreateQueue(ctx);

        foreach (var q in new[] { qDelete, qMarker })
        {
            await SetQueuePolicy(ctx, q.Url, AllowServiceSend(q.Arn, "s3.amazonaws.com", bucketArn));
        }

        var prefixFilter = new Filter { S3KeyFilter = new S3KeyFilter { FilterRules = [new FilterRule("prefix", Prefix)] } };
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucketName,
            QueueConfigurations =
            [
                new QueueConfiguration { Id = "permanent-delete-only", Queue = qDelete.Arn, Events = [new EventType("s3:ObjectRemoved:Delete")], Filter = prefixFilter },
                new QueueConfiguration { Id = "delete-marker-only", Queue = qMarker.Arn, Events = [new EventType("s3:ObjectRemoved:DeleteMarkerCreated")], Filter = prefixFilter },
            ],
        });
        var got = await s3.GetBucketNotificationAsync(bucketName);
        var events = (got.QueueConfigurations ?? []).ToDictionary(c => c.Id, c => string.Join(",", (c.Events ?? []).Select(e => e.Value)));
        Ensure(
            events.Count == 2
                && events.GetValueOrDefault("permanent-delete-only") == "s3:ObjectRemoved:Delete"
                && events.GetValueOrDefault("delete-marker-only") == "s3:ObjectRemoved:DeleteMarkerCreated",
            $"GetBucketNotificationConfiguration reports {Py.Repr(events)}; expected the two disjoint " +
            "single-event removal subscriptions this check installed");

        // Drain the configuration-time s3:TestEvent from both queues so the queue-depth observable below
        // measures only notification traffic; each drain ends once its queue's TestEvent arrives.
        foreach (var q in new[] { qDelete, qMarker })
        {
            await WaitFor(ctx, q.Url, b => b is JsonObject o && (string?)o["Event"] == "s3:TestEvent", timeoutS: 30.0);
        }

        var versionId = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucketName, Key = Key, ContentBody = BodyText })).VersionId;
        Ensure(
            !string.IsNullOrEmpty(versionId) && versionId != "null",
            $"PutObject into a versioning-enabled bucket returned VersionId {Py.Repr(versionId)}");

        // --- Leg 1: a version-less delete creates a marker, which reaches ONLY the marker queue.
        var started = FidelityTime.StartNew();
        var marker = await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucketName, Key = Key });
        Ensure(
            marker.DeleteMarker == "true",
            $"DeleteObject without a VersionId returned DeleteMarker {Py.Repr(marker.DeleteMarker)}, expected True");
        var markerVersion = marker.VersionId;
        var (matched, others) = await WaitFor(ctx, qMarker.Url, b => HasRecord(b, Key), timeoutS: 120.0);
        Ensure(
            matched.Count > 0,
            $"the delete marker on {Py.Repr(Key)} produced no notification on the " +
            "s3:ObjectRemoved:DeleteMarkerCreated destination within 120s; traffic seen: " +
            $"{Py.Repr(others.Take(2).Select(m => m.Body).ToList())}");
        var markerLatencyS = started.Elapsed.TotalSeconds;
        var record = Record(Body(matched[0]), Key);
        Ensure(
            (string?)record["eventName"] == "ObjectRemoved:DeleteMarkerCreated",
            $"the delete-marker notification carries eventName {Py.Repr((string?)record["eventName"])}, expected " +
            "'ObjectRemoved:DeleteMarkerCreated'");
        var obj = record["s3"]!["object"]!.AsObject();
        Ensure(
            (string?)obj["versionId"] == markerVersion,
            $"the notification reports versionId {Py.Repr((string?)obj["versionId"])}; the " +
            $"DeleteObject response returned the marker id {Py.Repr(markerVersion)}");
        Ensure(
            !obj.ContainsKey("size"),
            $"the delete-marker notification carries an object size {obj["size"]?.ToJsonString()}; a removal record has no size");

        var holdS = Math.Min(Math.Max(3.0 * markerLatencyS, 15.0), HoldCapS);
        await AssertQuiet(
            ctx,
            sqs,
            qDelete,
            holdS: holdS,
            reason: "a delete marker is not a permanent delete, and the marker event reached the other " +
                $"queue in {markerLatencyS:F1}s, so a leaked copy would have arrived by now");

        // --- Leg 2: a version-qualified delete reaches ONLY the permanent-delete queue. This both
        // re-proves the Delete queue was live during leg 1's window and controls leg 2's own absence.
        started = FidelityTime.StartNew();
        var permanent = await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucketName, Key = Key, VersionId = versionId });
        Ensure(
            permanent.DeleteMarker != "true",
            $"deleting version {versionId} reported DeleteMarker {Py.Repr(permanent.DeleteMarker)}; a version-qualified " +
            "delete removes the version rather than creating a marker");
        (matched, others) = await WaitFor(ctx, qDelete.Url, b => HasRecord(b, Key), timeoutS: 120.0);
        Ensure(
            matched.Count > 0,
            $"the permanent delete of version {versionId} produced no notification on the " +
            "s3:ObjectRemoved:Delete destination within 120s — without this leg the empty window above " +
            "would only mean that destination was never wired; traffic seen: " +
            $"{Py.Repr(others.Take(2).Select(m => m.Body).ToList())}");
        var deleteLatencyS = started.Elapsed.TotalSeconds;
        record = Record(Body(matched[0]), Key);
        Ensure(
            (string?)record["eventName"] == "ObjectRemoved:Delete",
            $"the permanent-delete notification carries eventName {Py.Repr((string?)record["eventName"])}, expected " +
            "'ObjectRemoved:Delete' — the two removal sub-types are being collapsed into one");
        Ensure(
            (string?)record["s3"]!["object"]!["versionId"] == versionId,
            $"the notification reports versionId {Py.Repr((string?)record["s3"]!["object"]!["versionId"])}; the " +
            $"deleted version was {Py.Repr(versionId)}");

        await AssertQuiet(
            ctx,
            sqs,
            qMarker,
            holdS: Math.Min(Math.Max(3.0 * deleteLatencyS, 15.0), HoldCapS),
            reason: "a permanent version delete is not a delete-marker creation, and the permanent-delete " +
                $"event reached the other queue in {deleteLatencyS:F1}s");

        // --- Independent read at the producer, through a third operation: the marker survived the
        // version delete and the version is gone.
        var versions = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucketName, Prefix = Prefix });
        var surviving = (versions.Versions ?? []).Where(v => v.IsDeleteMarker != true).Select(v => v.VersionId).ToList();
        Ensure(
            surviving.Count == 0,
            $"ListObjectVersions still reports object versions {Py.Repr(surviving)} after {versionId} was " +
            "permanently deleted");
        var markers = (versions.Versions ?? []).Where(v => v.IsDeleteMarker == true).Select(v => v.VersionId).ToList();
        Ensure(
            markers.SequenceEqual([markerVersion]),
            $"ListObjectVersions reports delete markers {Py.Repr(markers)}; expected exactly the one " +
            $"this check created ({Py.Repr(markerVersion)})");
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

    /// <summary>
    /// Drain a queue over a bounded window with long-polling receives; return every message seen. This
    /// is the NEGATIVE observable, meaningful only once a positive control has been observed.
    /// </summary>
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

    /// <summary>
    /// Poll a queue until a message whose parsed body satisfies predicate() arrives. Returns (matched,
    /// others); empty on timeout, and the caller asserts.
    /// </summary>
    private static async Task<(List<SqsMessage> Matched, List<SqsMessage> Others)> WaitFor(
        FidelityContext ctx, string queueUrl, Func<JsonNode?, bool> predicate, double timeoutS = 60.0)
    {
        var matched = new List<SqsMessage>();
        var others = new List<SqsMessage>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed.TotalSeconds < timeoutS && matched.Count == 0)
        {
            foreach (var msg in await Collect(ctx, queueUrl, windowS: Math.Min(8.0, timeoutS)))
            {
                (predicate(Body(msg)) ? matched : others).Add(msg);
            }
        }

        return (matched, others);
    }

    /// <summary>A poll's timeout: cap_s, bounded by the check's remaining time less 5 s.</summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>
    /// Assert a queue receives NO notification record for hold_s, on two observables: an empty
    /// long-poll drain and ApproximateNumberOfMessages polling to 0.
    /// </summary>
    private static async Task AssertQuiet(FidelityContext ctx, AmazonSQSClient sqs, Queue queue, double holdS, string reason)
    {
        var leaked = await Collect(ctx, queue.Url, windowS: holdS);
        var keys = leaked
            .Select(Body)
            .OfType<JsonObject>()
            .SelectMany(b => b["Records"]?.AsArray().OfType<JsonObject>() ?? [])
            .Select(r => (string?)r["s3"]?["object"]?["key"])
            .ToList();
        Ensure(keys.Count == 0, $"queue {queue.Name} received notification(s) for {Py.Repr(keys)} within {holdS:F0}s: {reason}");
        const double timeoutS = 90.0;
        var budgetS = BudgetS(ctx, timeoutS);
        var start = FidelityTime.StartNew();
        var stop = Math.Min(timeoutS, budgetS);
        while (true)
        {
            var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = queue.Url,
                AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
            })).Attributes;
            if (attrs["ApproximateNumberOfMessages"] == "0")
            {
                break;
            }

            if (start.Elapsed.TotalSeconds >= stop && budgetS < timeoutS)
            {
                throw new TimeoutException(
                    $"the check's time budget ran out after {start.Elapsed.TotalSeconds:F1}s, before the " +
                    $"{timeoutS}s counter wait ended ({Py.Repr(attrs)}); not graded");
            }

            Ensure(
                start.Elapsed.TotalSeconds < stop,
                $"queue {queue.Name} still reports {attrs["ApproximateNumberOfMessages"]} visible " +
                $"message(s) after its absence window, contradicting the empty drain: {reason}");
            await FidelityTime.Delay(TimeSpan.FromSeconds(3));
        }
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

    /// <summary>Create a tagged, versioning-enabled S3 bucket with teardown registered. Returns {name,arn}.</summary>
    private static async Task<(string Name, string Arn)> CreateVersionedBucket(FidelityContext ctx)
    {
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, $"{XsvcPrefix}bkt-", s3);
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = name,
            TagSet = [.. XsvcResourceTags().Select(t => new Tag { Key = t.Key, Value = t.Value })],
        });
        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = name,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });
        return (name, $"arn:aws:s3:::{name}");
    }
}
