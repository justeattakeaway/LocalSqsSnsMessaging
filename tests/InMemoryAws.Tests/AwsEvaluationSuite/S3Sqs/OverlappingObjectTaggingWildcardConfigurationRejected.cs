// Translated from behavioral/s3__sqs/check_overlapping_object_tagging_wildcard_configuration_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3Sqs;

/// <summary>s3__sqs.overlapping-object-tagging-wildcard-configuration-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class OverlappingObjectTaggingWildcardConfigurationRejected
{
    private const string Check = "overlapping-object-tagging-wildcard-configuration-rejected";
    private const string Key = "fam/overlap.txt";
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// A configuration whose `s3:ObjectTagging:*` overlaps an existing `s3:ObjectTagging:Put` is
    /// refused whole (InvalidArgument, "Configurations overlap") and the routing already in force
    /// keeps delivering; a fresh bucket accepting the wildcard alone proves it was a legal destination.
    /// </summary>
    [Test]
    [DisplayName("s3__sqs.overlapping-object-tagging-wildcard-configuration-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await CreateAclEnabledBucket(ctx);
        var controlBucket = await CreateAclEnabledBucket(ctx);
        var qPut = await CreateQueue(ctx);
        var qWild = await CreateQueue(ctx);
        await AllowS3Send(ctx, qPut, bucket.Arn);
        await AllowS3Send(ctx, qWild, bucket.Arn);
        await AllowS3Send(ctx, qWild, controlBucket.Arn);

        QueueConfiguration CfgA() => new() { Id = "tagging-put", Queue = qPut.Arn, Events = ["s3:ObjectTagging:Put"] };
        QueueConfiguration CfgB() => new() { Id = "tagging-any", Queue = qWild.Arn, Events = ["s3:ObjectTagging:*"] };

        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest { BucketName = bucket.Name, QueueConfigurations = [CfgA()] });
        // botocore reports ArgumentName and ArgumentValue, which the .NET SDK drops, so this call goes
        // over raw HTTP to read the same error document.
        var err = await S3Fidelity.PutNotificationConfigurationRaw(
            ctx,
            bucket.Name,
            S3Fidelity.QueueConfigurationXml(qPut.Arn, ["s3:ObjectTagging:Put"], "tagging-put")
            + S3Fidelity.QueueConfigurationXml(qWild.Arn, ["s3:ObjectTagging:*"], "tagging-any"))
            ?? throw Fail("the call succeeded; real AWS rejects it");
        Ensure(
            err.Code == "InvalidArgument",
            $"an overlapping ObjectTagging:Put + ObjectTagging:* configuration returned {err}, expected InvalidArgument");
        Ensure(
            err.Get("ArgumentName") == "Event",
            $"the overlap rejection names argument {Py.Repr(err.Get("ArgumentName"))}, expected 'Event': {err}");
        Ensure(
            err.Message == "Configurations overlap. Configurations on the same bucket cannot share a common event type.",
            $"unexpected overlap rejection message {Py.Repr(err.Message)}: {err}");
        Ensure(
            err.Get("ArgumentValue") == "s3:ObjectTagging:Put, s3:ObjectTagging:*",
            $"the overlap rejection names value {Py.Repr(err.Get("ArgumentValue"))}, expected " +
            $"'s3:ObjectTagging:Put, s3:ObjectTagging:*': {err}");

        var got = await s3.GetBucketNotificationAsync(bucket.Name);
        var installed = (got.QueueConfigurations ?? []).ToDictionary(c => c.Id, c => c.Events.Select(e => e.Value).ToList());
        Ensure(
            installed.Count == 1 && installed.TryGetValue("tagging-put", out var events) && events.SequenceEqual(["s3:ObjectTagging:Put"]),
            $"after the rejected replace the bucket reports {Py.Repr(installed)}; the rejected document must " +
            "not be partially applied and the previous one must survive");

        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket.Name, Key = Key, ContentBody = "overlap-subject" });
        var started = FidelityTime.StartNew();
        await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = bucket.Name,
            Key = Key,
            Tagging = new Tagging { TagSet = [new Amazon.S3.Model.Tag { Key = "round", Value = "1" }] },
        });
        var (recs, others) = await WaitForRecords(ctx, qPut.Url, Key, timeoutS: 120.0);
        Ensure(
            recs.Count > 0,
            $"the surviving s3:ObjectTagging:Put configuration delivered nothing within 120s; other traffic: {Py.Repr(others.Take(2).ToList())}");
        Ensure(
            (string?)recs[0]["eventName"] == "ObjectTagging:Put",
            $"the surviving route delivered eventName {Py.Repr((string?)recs[0]["eventName"])}, expected 'ObjectTagging:Put'");
        var latencyS = started.Elapsed.TotalSeconds;
        var holdS = Math.Min(Math.Max(3.0 * latencyS, 15.0), 120.0);
        var leaked = await DrainRecords(ctx, qWild.Url, holdS);
        Ensure(
            leaked.Count == 0,
            $"the wildcard queue from the REJECTED configuration received {Py.Repr(leaked.Select(l => new[] { l.EventName, l.Key }).ToList())} within " +
            $"{holdS:F0}s, while the surviving route delivered in {latencyS:F1}s");

        // Positive control: B on its own is legal and deliverable (fresh bucket: no cached routing).
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest { BucketName = controlBucket.Name, QueueConfigurations = [CfgB()] });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = controlBucket.Name, Key = Key, ContentBody = "overlap-control" });
        await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = controlBucket.Name,
            Key = Key,
            Tagging = new Tagging { TagSet = [new Amazon.S3.Model.Tag { Key = "round", Value = "2" }] },
        });
        (recs, others) = await WaitForRecords(ctx, qWild.Url, Key, timeoutS: 120.0);
        Ensure(
            recs.Count > 0,
            "the wildcard configuration, once installed alone, delivered nothing within 120s, so its " +
            $"empty hold above proves nothing; other traffic: {Py.Repr(others.Take(2).ToList())}");
        Ensure(
            (string?)recs[0]["s3"]?["bucket"]?["name"] == controlBucket.Name,
            $"the control delivery names bucket {Py.Repr((string?)recs[0]["s3"]?["bucket"]?["name"])}, expected {Py.Repr(controlBucket.Name)}");
        Ensure(
            (string?)recs[0]["eventName"] == "ObjectTagging:Put",
            $"s3:ObjectTagging:* delivered eventName {Py.Repr((string?)recs[0]["eventName"])} for a tag write, " +
            "expected 'ObjectTagging:Put'");
    }

    private sealed record Bucket(string Name, string Arn);

    private sealed record Queue(string Name, string Url, string Arn)
    {
        public List<string> Sources { get; } = [];
    }

    private static List<Amazon.S3.Model.Tag> XsvcTagsKv() =>
        [new() { Key = ManagedTagKey, Value = "true" }, new() { Key = "sim2real:check", Value = Check }];

    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>Create a tagged SQS queue (registers delete_queue teardown). Returns {name,url,arn}.</summary>
    private static async Task<Queue> CreateQueue(FidelityContext ctx)
    {
        var sqs = ctx.Sqs;
        var name = XName("q");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = new() { [ManagedTagKey] = "true" } })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = await S3Fidelity.NotificationQueueArn(sqs, url);
        return new Queue(name, url, arn);
    }

    /// <summary>Authorise s3.amazonaws.com to SendMessage, scoped to every bucket ARN granted so far.</summary>
    private static Task AllowS3Send(FidelityContext ctx, Queue queue, string bucketArn)
    {
        queue.Sources.Add(bucketArn);
        var sources = new JsonArray();
        foreach (var source in queue.Sources)
        {
            sources.Add((JsonNode?)source);
        }

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
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = sources } },
            }),
        };
        return ctx.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queue.Url,
            Attributes = new() { ["Policy"] = policy.ToJsonString() },
        });
    }

    /// <summary>
    /// A tagged bucket with ObjectOwnership=BucketOwnerPreferred, so object ACL writes are legal (the
    /// default BucketOwnerEnforced rejects PutObjectAcl with AccessControlListNotSupported).
    /// </summary>
    private static async Task<Bucket> CreateAclEnabledBucket(FidelityContext ctx)
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
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = name, TagSet = XsvcTagsKv() });
        return new Bucket(name, $"arn:aws:s3:::{name}");
    }

    private static List<JsonNode> RecordsIn(Message msg)
    {
        try
        {
            return JsonNode.Parse(msg.Body ?? string.Empty) is JsonObject body && body["Records"] is JsonArray records
                ? [.. records.OfType<JsonNode>()]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Long-poll a queue until a Records entry for `key` arrives. Returns (records, others): every
    /// record for `key` in the batch that satisfied the wait, plus every other body seen.
    /// </summary>
    private static async Task<(List<JsonNode> Records, List<string> Others)> WaitForRecords(FidelityContext ctx, string queueUrl, string key, double timeoutS)
    {
        var sqs = ctx.Sqs;
        var others = new List<string>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed.TotalSeconds < timeoutS)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            var found = new List<JsonNode>();
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(queueUrl, msg.ReceiptHandle);
                var recs = RecordsIn(msg).Where(r => (string?)r["s3"]?["object"]?["key"] == key).ToList();
                if (recs.Count > 0)
                {
                    found.AddRange(recs);
                }
                else
                {
                    var body = msg.Body ?? string.Empty;
                    others.Add(body.Length > 200 ? body[..200] : body);
                }
            }

            if (found.Count > 0)
            {
                return (found, others);
            }
        }

        return ([], others);
    }

    /// <summary>Drain a queue for hold_s; return (eventName, key) for every notification record seen.</summary>
    private static async Task<List<(string? EventName, string? Key)>> DrainRecords(FidelityContext ctx, string queueUrl, double holdS)
    {
        var sqs = ctx.Sqs;
        var seen = new List<(string?, string?)>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed.TotalSeconds < holdS)
        {
            var wait = (int)Math.Min(5, Math.Max(1, holdS - started.Elapsed.TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = wait });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(queueUrl, msg.ReceiptHandle);
                seen.AddRange(RecordsIn(msg).Select(r => ((string?)r["eventName"], (string?)r["s3"]?["object"]?["key"])));
            }
        }

        return seen;
    }
}
