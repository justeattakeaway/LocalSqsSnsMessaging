using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3Sqs;

/// <summary>
/// The helpers the <c>s3__sqs</c> "event family" checks share word for word: an ACL-enabled bucket
/// with four queues, each subscribed to exactly one of <c>s3:ObjectTagging:Put</c>,
/// <c>s3:ObjectTagging:Delete</c>, <c>s3:ObjectAcl:Put</c> and <c>s3:ObjectCreated:Copy</c>; the
/// record wait; and the drain proving the siblings stayed quiet.
/// </summary>
internal static class S3EventFamilyQueues
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>label -> the ONE event type that label's queue subscribes to.</summary>
    public static readonly (string Label, string Event)[] Families =
    [
        ("tagging-put", "s3:ObjectTagging:Put"),
        ("tagging-delete", "s3:ObjectTagging:Delete"),
        ("acl-put", "s3:ObjectAcl:Put"),
        ("created-copy", "s3:ObjectCreated:Copy"),
    ];

    public sealed record Queue(string Name, string Url, string Arn);

    public sealed record Bucket(string Name, string Arn);

    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>Create a tagged SQS queue (registers delete_queue teardown). Returns {name,url,arn}.</summary>
    public static async Task<Queue> CreateQueue(FidelityContext ctx)
    {
        var sqs = ctx.Sqs;
        var name = XName("q");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = new() { [ManagedTagKey] = "true" } })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        return new Queue(name, url, arn);
    }

    public static Task AllowS3Send(FidelityContext ctx, Queue queue, string bucketArn)
    {
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "AllowS3Send",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "s3.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = queue.Arn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = bucketArn } },
            }),
        };
        return ctx.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queue.Url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
    }

    /// <summary>
    /// A tagged bucket with ObjectOwnership=BucketOwnerPreferred, so object ACL writes are legal
    /// (the default BucketOwnerEnforced rejects PutObjectAcl with AccessControlListNotSupported).
    /// </summary>
    public static async Task<Bucket> CreateAclEnabledBucket(FidelityContext ctx, string check)
    {
        var s3 = ctx.S3;
        var name = XName("bkt");
        var request = new PutBucketRequest { BucketName = name, UseClientRegion = false, ObjectOwnership = ObjectOwnership.BucketOwnerPreferred };
        if (ctx.Region != "us-east-1")
        {
            request.PutBucketConfiguration = new PutBucketConfiguration { LocationConstraint = new BucketLocationConstraint(ctx.Region) };
        }

        await s3.PutBucketAsync(request);
        ctx.OnCleanup(() => S3Fidelity.EmptyAndDelete(s3, name));
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = name,
            TagSet = [new Amazon.S3.Model.Tag { Key = ManagedTagKey, Value = "true" }, new Amazon.S3.Model.Tag { Key = "sim2real:check", Value = check }],
        });
        return new Bucket(name, $"arn:aws:s3:::{name}");
    }

    /// <summary>
    /// One queue per family, each subscribed to exactly its one event type, plus any
    /// <paramref name="extra"/> queue configurations (whose queues the caller created and authorised).
    /// </summary>
    public static async Task<Dictionary<string, Queue>> WireFamilyQueues(FidelityContext ctx, Bucket bucket, List<QueueConfiguration>? extra = null)
    {
        var s3 = ctx.S3;
        var queues = new Dictionary<string, Queue>();
        foreach (var (label, _) in Families)
        {
            var q = await CreateQueue(ctx);
            await AllowS3Send(ctx, q, bucket.Arn);
            queues[label] = q;
        }

        var configs = Families.Select(f => new QueueConfiguration { Id = f.Label, Queue = queues[f.Label].Arn, Events = [new EventType(f.Event)] }).ToList();
        configs.AddRange(extra ?? []);
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest { BucketName = bucket.Name, QueueConfigurations = configs });
        var got = await s3.GetBucketNotificationAsync(bucket.Name);
        var installed = (got.QueueConfigurations ?? []).ToDictionary(c => c.Id, c => Events(c));
        var expected = Families.ToDictionary(f => f.Label, f => "[" + Py.Repr(f.Event) + "]");
        foreach (var c in extra ?? [])
        {
            expected[c.Id] = Events(c);
        }

        Ensure(
            installed.Count == expected.Count && installed.All(i => expected.TryGetValue(i.Key, out var e) && e == i.Value),
            $"GetBucketNotificationConfiguration reports {Py.Repr(installed)}; expected {Py.Repr(expected)}");
        return queues;
    }

    private static string Events(QueueConfiguration c) => "[" + string.Join(", ", (c.Events ?? []).Select(e => Py.Repr(e.Value))) + "]";

    private static List<JsonObject> RecordsIn(SqsMessage msg)
    {
        try
        {
            return JsonNode.Parse(msg.Body ?? string.Empty) is JsonObject body && body["Records"] is JsonArray records
                ? [.. records.OfType<JsonObject>()]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary><c>r.get("s3", {}).get("object", {}).get("key")</c>.</summary>
    public static string? KeyOf(JsonObject record) => (string?)record["s3"]?["object"]?["key"];

    /// <summary><c>r.get("eventName")</c>.</summary>
    public static string? NameOf(JsonObject record) => (string?)record["eventName"];

    /// <summary>
    /// Long-poll a queue until a Records entry for <paramref name="key"/> arrives. Returns (records,
    /// others): every record for the key in the batch that satisfied the wait, plus every other body seen.
    /// </summary>
    public static async Task<(List<JsonObject> Records, List<string> Others)> WaitForRecords(FidelityContext ctx, string queueUrl, string key, double timeoutS)
    {
        var sqs = ctx.Sqs;
        var others = new List<string>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed.TotalSeconds < timeoutS)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            var found = new List<JsonObject>();
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(queueUrl, msg.ReceiptHandle);
                var recs = RecordsIn(msg).Where(r => KeyOf(r) == key).ToList();
                if (recs.Count > 0)
                {
                    found.AddRange(recs);
                }
                else
                {
                    var body = msg.Body ?? string.Empty;
                    others.Add(body[..Math.Min(200, body.Length)]);
                }
            }

            if (found.Count > 0)
            {
                return (found, others);
            }
        }

        return ([], others);
    }

    /// <summary>
    /// Round-robin drain every family queue for <paramref name="holdS"/>. The non-subject queues must
    /// receive no notification record at all; any late copy on the subject queue must carry
    /// <paramref name="expectedName"/>. Then each non-subject queue must report
    /// ApproximateNumberOfMessages == 0.
    /// </summary>
    public static async Task AssertFamilyQuiet(
        FidelityContext ctx, Dictionary<string, Queue> queues, string subject, string expectedName, double holdS, double latencyS)
    {
        var sqs = ctx.Sqs;
        var leaked = queues.Keys.ToDictionary(label => label, _ => new List<(string? Name, string? Key)>());
        var started = FidelityTime.StartNew();
        while (started.Elapsed.TotalSeconds < holdS)
        {
            foreach (var (label, q) in queues)
            {
                var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = q.Url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
                foreach (var msg in resp.Messages ?? [])
                {
                    await sqs.DeleteMessageAsync(q.Url, msg.ReceiptHandle);
                    leaked[label].AddRange(RecordsIn(msg).Select(r => (NameOf(r), KeyOf(r))));
                }
            }
        }

        var late = leaked[subject];
        leaked.Remove(subject);
        Ensure(
            late.All(l => l.Name == expectedName),
            $"a later copy on the {subject} queue carried {Render(late)}; every copy must be {Py.Repr(expectedName)}");
        Ensure(
            leaked.Values.All(v => v.Count == 0),
            $"sibling queues received notification records within {holdS:F0}s: " +
            "{" + string.Join(", ", leaked.Select(l => $"{Py.Repr(l.Key)}: {Render(l.Value)}")) + "}; the " +
            $"subject event reached its own queue in {latencyS:F1}s, so these are mis-routed, and " +
            "each sibling subscribes to exactly one other event type: " +
            "{" + string.Join(", ", Families.Where(f => f.Label != subject).Select(f => $"{Py.Repr(f.Label)}: {Py.Repr(f.Event)}")) + "}");
        foreach (var (label, q) in queues)
        {
            if (label == subject)
            {
                continue;
            }

            var depth = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = q.Url, AttributeNames = ["ApproximateNumberOfMessages"] }))
                .Attributes["ApproximateNumberOfMessages"];
            Ensure(depth == "0", $"the {label} queue reports {depth} visible message(s) after an empty drain");
        }
    }

    private static string Render(List<(string? Name, string? Key)> items) =>
        "[" + string.Join(", ", items.Select(i => $"({Py.Repr(i.Name)}, {Py.Repr(i.Key)})")) + "]";

    /// <summary><c>s3.get_object_tagging(...)["TagSet"]</c> as Python prints it.</summary>
    public static async Task<string> TagSet(IAmazonS3 s3, string bucket, string key)
    {
        var tags = (await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key })).Tagging ?? [];
        return "[" + string.Join(", ", tags.Select(t => $"{{'Key': {Py.Repr(t.Key)}, 'Value': {Py.Repr(t.Value)}}}")) + "]";
    }
}
