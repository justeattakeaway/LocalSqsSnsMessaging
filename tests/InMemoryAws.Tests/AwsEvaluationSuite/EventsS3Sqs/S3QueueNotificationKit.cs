using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>
/// The helpers the <c>events__s3__sqs</c> checks about S3's own queue notifications repeat word for
/// word: the <c>sim2real-xsvc-</c> bucket and queue, the queue policy granting S3 SendMessage, the
/// s3:TestEvent filter, the record polls, and the teardown that proves each resource gone.
/// </summary>
internal static class S3QueueNotificationKit
{
    public const string XsvcPrefix = "sim2real-xsvc-";
    public const string ManagedTagKey = "sim2real:managed";

    /// <summary>The SQS not-found codes (<c>_SQS_NF</c>).</summary>
    public static readonly string[] SqsNotFound = ["AWS.SimpleQueueService.NonExistentQueue", "QueueDoesNotExist"];

    public const string SqsGone = "The specified queue does not exist.";

    public sealed record Queue(string Name, string Url, string Arn);

    public sealed record Bucket(string Name, string Arn);

    public static Dictionary<string, string> XsvcResourceTags() =>
        new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    public static List<Amazon.S3.Model.Tag> XsvcTagsKv() =>
        [.. XsvcResourceTags().Select(t => new Amazon.S3.Model.Tag { Key = t.Key, Value = t.Value })];

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    public static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>
    /// True for S3's one-off destination-validation handshake message: when a queue is first
    /// attached as an S3 notification destination, S3 posts {"Service":"Amazon S3","Event":
    /// "s3:TestEvent", ...}. It carries no "Records" and is NOT an object event.
    /// </summary>
    public static bool IsS3TestEvent(JsonNode? body) =>
        body is JsonObject o && o["Event"] is JsonValue v && v.TryGetValue<string>(out var e) && e == "s3:TestEvent";

    /// <summary><c>json.loads(m["Body"])</c>, falling back to the raw string.</summary>
    public static JsonNode? Body(SqsMessage msg)
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

    /// <summary><c>body.get("Records", [])</c>.</summary>
    public static List<JsonObject> Records(JsonNode? body) =>
        body is JsonObject o && o["Records"] is JsonArray records ? [.. records.OfType<JsonObject>()] : [];

    /// <summary><c>record["s3"]["object"]</c>.</summary>
    public static JsonObject Object(JsonObject record) => record["s3"]!["object"]!.AsObject();

    /// <summary><c>d.get(name)</c> as a string; null when absent.</summary>
    public static string? Str(JsonNode? node, string name) =>
        node is JsonObject o && o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Create an SQS queue (registers delete_queue teardown). Returns {name,url,arn}.</summary>
    public static async Task<Queue> CreateQueue(FidelityContext ctx, bool fifo = false, bool tolerant = false)
    {
        var baseName = XName("q");
        var name = fifo ? $"{baseName}.fifo" : baseName;
        var sqs = ctx.Sqs;
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            attrs["FifoQueue"] = "true";
        }

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = attrs, Tags = XsvcResourceTags() })).QueueUrl;
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        ctx.OnCleanup(tolerant ? Tolerant(() => sqs.DeleteQueueAsync(url), SqsNotFound) : () => sqs.DeleteQueueAsync(url));
        return new Queue(name, url, arn);
    }

    /// <summary>
    /// Create an S3 bucket (registers empty-then-delete teardown) and tag it (a tagging failure is
    /// ignored unless <paramref name="tagStrictly"/>). Returns {name,arn}.
    /// </summary>
    public static async Task<Bucket> CreateBucket(FidelityContext ctx, bool tagStrictly = false)
    {
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, XsvcPrefix + "bkt-", s3);
        try
        {
            await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = name, TagSet = XsvcTagsKv() });
        }
        catch (AmazonS3Exception) when (!tagStrictly)
        {
        }

        return new Bucket(name, $"arn:aws:s3:::{name}");
    }

    public static JsonObject SqsPolicyForS3(string queueArn, string bucketArn) => new()
    {
        ["Version"] = "2012-10-17",
        ["Statement"] = new JsonArray(new JsonObject
        {
            ["Sid"] = "AllowS3SendMessage",
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["Service"] = "s3.amazonaws.com" },
            ["Action"] = "sqs:SendMessage",
            ["Resource"] = queueArn,
            ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = bucketArn } },
        }),
    };

    public static Task SetQueuePolicy(FidelityContext ctx, string queueUrl, JsonObject policy) =>
        ctx.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new() { ["Policy"] = policy.ToJsonString() },
        });

    /// <summary>A queue configuration of PutBucketNotificationConfiguration.</summary>
    public static QueueConfiguration QueueConfig(string queueArn, IEnumerable<string> events, string? id = null, params (string Name, string Value)[] filterRules)
    {
        var config = new QueueConfiguration { Queue = queueArn, Events = [.. events.Select(e => new EventType(e))], Id = id };
        if (filterRules.Length > 0)
        {
            config.Filter = new Filter { S3KeyFilter = new S3KeyFilter { FilterRules = [.. filterRules.Select(r => new FilterRule(r.Name, r.Value))] } };
        }

        return config;
    }

    /// <summary>
    /// <c>_wait_for_s3_records</c>: poll SQS for real S3 object-event notifications, discarding
    /// s3:TestEvent; raises when fewer than <paramref name="expectedCount"/> arrive in time.
    /// </summary>
    public static async Task<List<JsonNode?>> WaitForS3Records(FidelityContext ctx, string queueUrl, int expectedCount = 1, double timeoutS = 90)
    {
        var sqs = ctx.Sqs;
        var started = FidelityTime.StartNew();
        var output = new List<JsonNode?>();
        while (output.Count < expectedCount && started.Elapsed.TotalSeconds < timeoutS)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"],
            });
            foreach (var m in resp.Messages ?? [])
            {
                var body = Body(m);
                await sqs.DeleteMessageAsync(queueUrl, m.ReceiptHandle);
                if (IsS3TestEvent(body))
                {
                    continue;
                }

                output.Add(body);
            }
        }

        if (output.Count < expectedCount)
        {
            throw Fail($"expected {expectedCount} S3 notification(s), got {output.Count} within {timeoutS}s");
        }

        return output;
    }

    /// <summary>
    /// <c>_receive_records</c>: every S3 object-event record the queue delivers, discarding
    /// s3:TestEvent, until <paramref name="done"/> holds (bounded by <paramref name="timeoutS"/>),
    /// then for a further <paramref name="quietS"/> quiet window.
    /// </summary>
    public static async Task<List<JsonObject>> ReceiveRecords(
        FidelityContext ctx, string queueUrl, Func<List<JsonObject>, bool> done, string awaited, double timeoutS = 40, double quietS = 10.0)
    {
        var sqs = ctx.Sqs;
        var clock = FidelityTime.StartNew();
        var deadline = timeoutS;
        double? quietUntil = null;
        var output = new List<JsonObject>();
        while (true)
        {
            var now = clock.Elapsed.TotalSeconds;
            if (quietUntil is null && done(output))
            {
                quietUntil = now + quietS;
            }

            if (quietUntil is not null && now >= quietUntil)
            {
                return output;
            }

            if (quietUntil is null && now >= deadline)
            {
                var seen = output.Select(r => (Str(r, "eventName"), Str(Object(r), "key"))).Order().ToList();
                throw Fail($"within {timeoutS:F0}s the queue delivered {Py.Repr(seen)}, awaiting {Py.Repr(awaited)}");
            }

            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
            foreach (var m in resp.Messages ?? [])
            {
                var body = JsonNode.Parse(m.Body);
                await sqs.DeleteMessageAsync(queueUrl, m.ReceiptHandle);
                if (IsS3TestEvent(body))
                {
                    continue;
                }

                output.AddRange(Records(body));
            }
        }
    }

    /// <summary>
    /// <c>_distinct</c>: one record per event (S3 delivers at least once, so a redelivery repeats the
    /// same eventName, key, and versionId or sequencer).
    /// </summary>
    public static List<JsonObject> Distinct(List<JsonObject> records)
    {
        var seen = new Dictionary<(string?, string?, string?), JsonObject>();
        foreach (var r in records)
        {
            var obj = Object(r);
            var identity = (Str(r, "eventName"), Str(obj, "key"), Str(obj, "versionId") is { Length: > 0 } v ? v : Str(obj, "sequencer"));
            seen.TryAdd(identity, r);
        }

        return [.. seen.Values];
    }

    /// <summary>PutBucketNotificationConfiguration must succeed once S3 may send to the queue (retried for InvalidArgument for 30 s).</summary>
    public static async Task PutNotificationAfterPolicy(FidelityContext ctx, string bucket, List<QueueConfiguration> queueConfigurations)
    {
        var started = FidelityTime.StartNew();
        while (true)
        {
            var err = await FidelityContext.CaptureError(() => ctx.S3.PutBucketNotificationAsync(new PutBucketNotificationRequest
            {
                BucketName = bucket,
                QueueConfigurations = queueConfigurations,
            }));
            if (err is null)
            {
                return;
            }

            if (err.Code != "InvalidArgument" || started.Elapsed.TotalSeconds >= 30.0)
            {
                throw err.Exception;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>A teardown that ignores only the given not-found codes (the body already deleted it).</summary>
    public static Func<Task> Tolerant(Func<Task> fn, params string[] codes) => async () =>
    {
        try
        {
            await fn();
        }
        catch (Amazon.Runtime.AmazonServiceException ex) when (codes.Contains(AwsError.From(ex).Code))
        {
        }
    };

    /// <summary>
    /// <c>_refused</c>: <paramref name="fn"/> must fail with one of <paramref name="codes"/> in the
    /// HTTP class of <paramref name="status"/> (exactly <paramref name="status"/> when
    /// <paramref name="exact"/>), with exactly <paramref name="message"/>.
    /// </summary>
    /// <summary>
    /// <c>Error.Code</c> as botocore reports it: for a bodiless error response (a HEAD request's),
    /// the bare HTTP status, where the .NET SDK names the status.
    /// </summary>
    private static string? BotocoreCode(AwsError err) =>
        S3CoreFidelity.BotocoreMessage(err) != err.Message ? ((int)err.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture) : err.Code;

    public static async Task Refused(Func<Task> fn, string[] codes, int status, string what, string message, bool exact = false)
    {
        var err = await FidelityContext.CaptureError(fn)
            ?? throw Fail($"{what}: expected {string.Join("/", codes)} HTTP {status}, the call succeeded");
        var got = (int)err.StatusCode;
        var want = exact ? status.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{status / 100}xx";
        var inClass = exact ? got == status : got / 100 == status / 100;
        // botocore's message for a bodiless (HEAD) refusal is the reason phrase.
        var msg = S3CoreFidelity.BotocoreMessage(err) ?? string.Empty;
        var code = BotocoreCode(err);
        Ensure(
            codes.Contains(code) && inClass,
            $"{what}: expected {string.Join("/", codes)} HTTP {want}, got {Py.Repr(code)} HTTP {got}: {Py.Repr(msg[..Math.Min(150, msg.Length)])}");
        Ensure(msg == message, $"{what}: {code} message: expected {Py.Repr(message)}, got {Py.Repr(msg)}");
    }

    /// <summary>
    /// <c>_poll_refused</c>: poll <paramref name="fn"/> until it reaches the terminal not-found
    /// answer, then grade it with <see cref="Refused"/>. A success while the delete converges is
    /// retried; any other error fails at once.
    /// </summary>
    public static async Task PollRefused(Func<Task> fn, string[] codes, int status, string what, string message, double timeoutS, bool exact = false)
    {
        var started = FidelityTime.StartNew();
        while (true)
        {
            var err = await FidelityContext.CaptureError(fn);
            if (err is not null)
            {
                if (codes.Contains(BotocoreCode(err)))
                {
                    await Refused(fn, codes, status, what, message, exact);
                    return;
                }

                throw err.Exception;
            }

            if (started.Elapsed.TotalSeconds >= timeoutS)
            {
                throw Fail($"{what}: still readable {timeoutS:F0}s after the delete");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary><c>_poll_absent</c>: poll <paramref name="listing"/> until it is empty (bounded).</summary>
    public static async Task PollAbsent(Func<Task<List<string>>> listing, string what, double timeoutS)
    {
        var started = FidelityTime.StartNew();
        while (true)
        {
            var last = await listing();
            if (last.Count == 0)
            {
                return;
            }

            if (started.Elapsed.TotalSeconds >= timeoutS)
            {
                throw Fail($"{what} {timeoutS:F0}s after the delete: {Py.Repr(last)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary><c>_gone_queue</c>: delete the queue and prove GetQueueUrl, ListQueues and SendMessage no longer find it.</summary>
    public static async Task GoneQueue(FidelityContext ctx, string name, string url, double goneS)
    {
        var sqs = ctx.Sqs;
        await sqs.DeleteQueueAsync(url);
        // AWS documents up to 60 s for a deleted queue to stop answering.
        await PollRefused(() => sqs.GetQueueUrlAsync(name), SqsNotFound, 400, $"GetQueueUrl {name}", SqsGone, goneS);
        // The listing and the URL need not converge at the same instant: each is polled.
        await PollAbsent(
            async () => [.. ((await sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = name })).QueueUrls ?? [])
                .Where(u => u.TrimEnd('/').Split('/')[^1] == name)],
            $"ListQueues still lists deleted queue {name}",
            goneS);
        await PollRefused(() => sqs.SendMessageAsync(url, "gone"), SqsNotFound, 400, $"SendMessage {name}", SqsGone, goneS);
    }

    private static async Task EmptyBucket(IAmazonS3 s3, string bucket)
    {
        var versions = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket });
        var objs = (versions.Versions ?? []).Select(v => new KeyVersion { Key = v.Key, VersionId = v.VersionId }).ToList();
        if (objs.Count > 0)
        {
            await s3.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = bucket, Objects = objs, Quiet = true });
        }
    }

    /// <summary>
    /// <c>_gone_bucket</c>: empty and delete the bucket and prove HeadBucket, ListBuckets and
    /// PutObject no longer find it. <paramref name="headCodes"/> are the codes the check accepts
    /// from HeadBucket, which has no body (botocore reports the bare status, <c>404</c>).
    /// </summary>
    public static async Task GoneBucket(FidelityContext ctx, string bucket, string[] headCodes, double goneS)
    {
        var s3 = ctx.S3;
        await EmptyBucket(s3, bucket);
        await s3.DeleteBucketAsync(bucket);
        // HeadBucket has no body: the code is the bare status.
        await PollRefused(() => s3.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket }), headCodes, 404, $"HeadBucket {bucket}", "Not Found", goneS, exact: true);
        var names = ((await s3.ListBucketsAsync(new ListBucketsRequest { Prefix = bucket })).Buckets ?? []).Select(b => b.BucketName).ToHashSet();
        Ensure(!names.Contains(bucket), $"ListBuckets still lists deleted bucket {bucket}");
        await Refused(
            () => s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "gone", ContentBody = "x" }),
            ["NoSuchBucket"], 404, $"PutObject {bucket}", "The specified bucket does not exist");
    }
}
