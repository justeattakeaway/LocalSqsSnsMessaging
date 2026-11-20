// Translated from behavioral/s3__sqs/check_a_denied_put_creates_no_object_and_emits_no_notification.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3Sqs;

/// <summary>s3__sqs.a-denied-put-creates-no-object-and-emits-no-notification</summary>
[Category("AwsEvaluationSuite")]
public sealed class ADeniedPutCreatesNoObjectAndEmitsNoNotification
{
    private const string Check = "a-denied-put-creates-no-object-and-emits-no-notification";
    private const string AllowedPrefix = "allowed/";
    private const string DeniedPrefix = "denied/";
    private const string DenySid = "Sim2realDenyDeniedPrefixPut";

    /// <summary>
    /// A PutObject the bucket policy denies creates no object and emits no ObjectCreated
    /// notification, and the identical PutObject emits one once the Deny is removed.
    /// </summary>
    [Test]
    [DisplayName("s3__sqs.a-denied-put-creates-no-object-and-emits-no-notification")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var account = ctx.Account;
        var callerArn = ctx.CallerArn;
        const string checkTag = Check;
        var suffix = Guid.NewGuid().ToString("N")[..10];

        var bucket = await CreateBucket(ctx, checkTag);
        var (queueUrl, queueArn) = await MakeQueue(ctx, $"sim2real-nd-deniedput-{suffix}", bucket, account, checkTag);
        // The subscription is DELIBERATELY as wide as it can be: every object-creation event, no prefix,
        // no suffix. Nothing about the subscription can explain an absence.
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket,
            QueueConfigurations = [new QueueConfiguration { Id = "all-creates", Queue = queueArn, Events = [new EventType("s3:ObjectCreated:*")] }],
        });
        var stored = (await s3.GetBucketNotificationAsync(bucket)).QueueConfigurations ?? [];
        var storedEvents = stored.Select(c => (c.Events ?? []).Select(e => e.Value).ToList()).ToList();
        Ensure(
            storedEvents.Count == 1 && storedEvents[0].SequenceEqual(["s3:ObjectCreated:*"]),
            $"the stored notification configuration is {Py.Repr(storedEvents)}, expected a single unfiltered " +
            "s3:ObjectCreated:* subscription — a narrower one would give the absence below a second " +
            "possible explanation");
        Ensure(
            stored[0].Queue == queueArn,
            $"the stored configuration points at {Py.Repr(stored[0].Queue)}, expected {Py.Repr(queueArn)}");
        await DrainRecords(ctx, queueUrl, seconds: 8); // discard S3's s3:TestEvent probe message

        // BEAT 1 — a permitted write, on this exact bucket and queue, measures L.
        var allowedKey = $"{AllowedPrefix}ok-{suffix}.bin";
        var payload = Enumerable.Repeat((byte)'z', 777).ToArray();
        var t0 = FidelityTime.StartNew();
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = allowedKey, InputStream = new MemoryStream(payload) });
        var records = await PollUntil(
            () => DrainRecords(ctx, queueUrl, seconds: 5),
            recs => recs.Any(r => r.Key == allowedKey),
            timeoutS: 180,
            intervalS: 0,
            description: $"the permitted write to {Py.Repr(allowedKey)} produced an ObjectCreated notification on " +
                $"{queueUrl} — the positive control that makes the absence below evidence");
        var latency = Math.Max(t0.Elapsed.TotalSeconds, 1.0);
        var good = records.First(r => r.Key == allowedKey);
        Ensure(
            good.EventName?.StartsWith("ObjectCreated:", StringComparison.Ordinal) == true,
            $"the notification for {Py.Repr(allowedKey)} carries eventName={Py.Repr(good.EventName)}, expected an " +
            "ObjectCreated:* value");
        Ensure(
            good.Size == payload.Length,
            $"the notification for {Py.Repr(allowedKey)} reports size={good.Size}, expected " +
            $"{payload.Length} — a record whose size does not match the committed bytes is not derived " +
            "from the committed write");

        // BREAK EXACTLY ONE THING — a resource-policy Deny scoped to one prefix, so cleanup and the
        // control prefix are untouched.
        await s3.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = bucket, Policy = DenyPolicy(bucket) });
        ctx.OnCleanup(() => DropPolicy(ctx, bucket));
        var attached = (await s3.GetBucketPolicyAsync(new GetBucketPolicyRequest { BucketName = bucket })).Policy;
        Ensure(
            attached.Contains(DenySid, StringComparison.Ordinal),
            $"PutBucketPolicy did not attach {Py.Repr(DenySid)} to {bucket}: {Py.Repr(attached)}");

        var deniedKey = $"{DeniedPrefix}blocked-{suffix}.bin";
        await PollUntil(
            () => PutStatus(ctx, bucket, deniedKey, payload),
            status => status == 403,
            timeoutS: 120,
            intervalS: 3,
            description: $"the bucket policy Deny took effect and PutObject on {Py.Repr(deniedKey)} is refused (S3 " +
                "policies are eventually consistent, so this is a bounded retry over the OBSERVED " +
                "behaviour rather than a fixed wait)");
        // The graded attempt goes through `_error`, which RAISES on success — so an over-permissive
        // target cannot pass off acceptance as a code mismatch.
        var (code, message) = await Error(() => ctx.S3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = deniedKey, InputStream = new MemoryStream(payload) }));
        Ensure(
            code == "AccessDenied",
            $"PutObject on {Py.Repr(deniedKey)} was refused with {Py.Repr(code)}, expected 'AccessDenied' for an " +
            "explicit resource-policy Deny");
        // S3 names the refused caller and the exact object ARN in an explicit-deny refusal.
        var expectedMessage =
            $"User: {callerArn} is not authorized to perform: s3:PutObject on resource: " +
            $"\"arn:aws:s3:::{bucket}/{deniedKey}\" with an explicit deny in a resource-based policy";
        Ensure(
            message == expectedMessage,
            $"PutObject on {Py.Repr(deniedKey)} was refused with message {Py.Repr(message)}, expected {Py.Repr(expectedMessage)}");

        // STRUCTURAL second observable: there is no object, so there is nothing to notify
        // about. This is what separates this check from a filter-based negative.
        var (headCode, headMessage) = await Error(() => ctx.S3.GetObjectMetadataAsync(bucket, deniedKey));
        Ensure(
            S3NotificationProbes.IsHeadNotFound(headCode, headMessage),
            $"head_object on the denied key {Py.Repr(deniedKey)} answered {Py.Repr(headCode)} / {Py.Repr(headMessage)}, " +
            "expected a not-found — if the object EXISTS the write was not actually denied and the " +
            "absence below would be a lost notification, not a withheld one");
        var listed = (await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = DeniedPrefix })).KeyCount;
        Ensure(listed == 0, $"ListObjectsV2 reports KeyCount={listed} under {Py.Repr(DeniedPrefix)}, expected 0");

        // THE NEGATIVE, held for 3L and confirmed by queue depth.
        var hold = Math.Max(3 * latency, 15.0);
        var leaked = await DrainRecords(ctx, queueUrl, seconds: hold);
        Ensure(
            leaked.Count == 0,
            $"the queue received {leaked.Count} notification record(s) during a {hold:F0}s hold (3x the " +
            $"{latency:F0}s measured latency) for writes that were REFUSED and created no object: " +
            $"{string.Join(", ", leaked)} — a notification emitted from the request rather than the committed write points " +
            "a consumer at an object that does not exist");
        var depth = await QueueDepth(ctx, queueUrl);
        Ensure(
            depth == 0,
            $"the queue reports depth {depth} behind an empty receive — the second independent observable disagrees with the first");

        // BEATS 5 and 6 — restore, then the IDENTICAL call on the SAME key must succeed AND notify.
        await s3.DeleteBucketPolicyAsync(new DeleteBucketPolicyRequest { BucketName = bucket });
        await PollUntil(
            () => PutStatus(ctx, bucket, deniedKey, payload),
            status => status == 200,
            timeoutS: 120,
            intervalS: 3,
            description: $"with the Deny removed, the identical PutObject on {Py.Repr(deniedKey)} is accepted again");
        var restored = await PollUntil(
            () => DrainRecords(ctx, queueUrl, seconds: 5),
            recs => recs.Any(r => r.Key == deniedKey),
            timeoutS: 180,
            intervalS: 0,
            description: $"the previously withheld notification for {Py.Repr(deniedKey)} is emitted once the write is " +
                "permitted — the restore beat, on the same key and the same wiring");
        var nowGood = restored.First(r => r.Key == deniedKey);
        Ensure(
            nowGood.EventName?.StartsWith("ObjectCreated:", StringComparison.Ordinal) == true,
            $"the restored notification carries eventName={Py.Repr(nowGood.EventName)}, expected an ObjectCreated:* value");
        Ensure(
            nowGood.Bucket == bucket && nowGood.Size == payload.Length,
            $"the restored notification reports bucket={Py.Repr(nowGood.Bucket)} size={nowGood.Size}, " +
            $"expected {Py.Repr(bucket)} and {payload.Length}");
        Ensure(
            (await s3.GetObjectMetadataAsync(bucket, deniedKey)).ContentLength == payload.Length,
            $"the object {Py.Repr(deniedKey)} does not exist at the expected size after the permitted write");
    }

    private sealed record Rec(string? EventName, string Key, string? Bucket, long? Size);

    /// <summary>A bounded retry that RAISES on its deadline; never returns its last value on timeout.</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, double intervalS = 3.0, string description = "condition")
    {
        var started = FidelityTime.StartNew();
        T? last = default;
        while (started.Elapsed.TotalSeconds < timeoutS)
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not met within {timeoutS}s. Last result: {last}");
    }

    /// <summary>The (code, message) of the error raised by fn(). RAISES if fn() unexpectedly SUCCEEDS.</summary>
    private static async Task<(string? Code, string? Message)> Error(Func<Task> fn)
    {
        var err = await FidelityContext.CaptureError(fn) ?? throw Fail("the call was expected to be rejected but it succeeded");
        return (err.Code, err.Message);
    }

    /// <summary>The HTTP status a PutObject answers with: 200 when accepted, the error's status when refused.</summary>
    private static async Task<int> PutStatus(FidelityContext ctx, string bucket, string key, byte[] body)
    {
        var err = await FidelityContext.CaptureError(() => ctx.S3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body) }));
        return err is null ? 200 : (int)err.StatusCode;
    }

    private static async Task<string> CreateBucket(FidelityContext ctx, string checkTag)
    {
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-nd-deniedput-", s3);
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = name,
            TagSet = [new Tag { Key = "sim2real:managed", Value = "true" }, new Tag { Key = "sim2real-check", Value = checkTag }],
        });
        return name;
    }

    private static async Task<(string Url, string Arn)> MakeQueue(FidelityContext ctx, string name, string bucket, string account, string checkTag)
    {
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = new() { ["sim2real-check"] = checkTag } })).QueueUrl;
        ctx.OnCleanup(() => ctx.Sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "AllowS3",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "s3.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn,
                ["Condition"] = new JsonObject
                {
                    ["StringEquals"] = new JsonObject { ["aws:SourceAccount"] = account },
                    ["ArnLike"] = new JsonObject { ["aws:SourceArn"] = $"arn:*:s3:::{bucket}" },
                },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (url, arn);
    }

    /// <summary>Every S3 event record seen, URL-decoded; an S3 test-event message carries no Records.</summary>
    private static async Task<List<Rec>> DrainRecords(FidelityContext ctx, string url, double seconds)
    {
        var sqs = ctx.Sqs;
        var output = new List<Rec>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed.TotalSeconds < seconds)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                var body = JsonNode.Parse(msg.Body)!.AsObject();
                foreach (var record in body["Records"]?.AsArray().OfType<JsonObject>() ?? [])
                {
                    var obj = record["s3"]!["object"]!;
                    output.Add(new Rec(
                        (string?)record["eventName"],
                        S3NotificationProbes.UnquotePlus((string)obj["key"]!),
                        (string?)record["s3"]!["bucket"]!["name"],
                        obj["size"] is JsonValue v && v.TryGetValue<long>(out var size) ? size : null));
                }

                await sqs.DeleteMessageAsync(url, msg.ReceiptHandle);
            }
        }

        return output;
    }

    private static async Task<int> QueueDepth(FidelityContext ctx, string url)
    {
        var attrs = (await ctx.Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible", "ApproximateNumberOfMessagesDelayed"],
        })).Attributes;
        return attrs.Values.Sum(v => int.Parse(v, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string DenyPolicy(string bucket) => new JsonObject
    {
        ["Version"] = "2012-10-17",
        ["Statement"] = new JsonArray(new JsonObject
        {
            ["Sid"] = DenySid,
            ["Effect"] = "Deny",
            ["Principal"] = "*",
            ["Action"] = "s3:PutObject",
            ["Resource"] = $"arn:aws:s3:::{bucket}/{DeniedPrefix}*",
        }),
    }.ToJsonString();

    private static async Task DropPolicy(FidelityContext ctx, string bucket)
    {
        try
        {
            await ctx.S3.DeleteBucketPolicyAsync(new DeleteBucketPolicyRequest { BucketName = bucket });
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "NoSuchBucketPolicy" or "NoSuchBucket")
        {
        }
    }
}
