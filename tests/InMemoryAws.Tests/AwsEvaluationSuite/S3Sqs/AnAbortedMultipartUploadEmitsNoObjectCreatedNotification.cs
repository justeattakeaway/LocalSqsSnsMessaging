// Translated from behavioral/s3__sqs/check_an_aborted_multipart_upload_emits_no_object_created_notification.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3Sqs;

/// <summary>s3__sqs.an-aborted-multipart-upload-emits-no-object-created-notification</summary>
[Category("AwsEvaluationSuite")]
public sealed class AnAbortedMultipartUploadEmitsNoObjectCreatedNotification
{
    private const string Check = "an-aborted-multipart-upload-emits-no-object-created-notification";

    private static readonly byte[] Part = [.. Enumerable.Repeat((byte)'m', 1024)];

    /// <summary>
    /// An aborted multipart upload emits no ObjectCreated notification even though its part was
    /// uploaded, while an identical completed upload emits exactly one CompleteMultipartUpload record.
    /// </summary>
    [Test]
    [DisplayName("s3__sqs.an-aborted-multipart-upload-emits-no-object-created-notification")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var account = ctx.Account;
        const string checkTag = Check;
        var suffix = Guid.NewGuid().ToString("N")[..10];

        var bucket = await CreateBucket(ctx, checkTag);
        var (queueUrl, queueArn) = await MakeQueue(ctx, $"sim2real-nd-abortmpu-{suffix}", bucket, account, checkTag);
        // As wide a subscription as S3 offers: every creation event, no prefix, no suffix.
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket,
            QueueConfigurations = [new QueueConfiguration { Id = "all-creates", Queue = queueArn, Events = [new EventType("s3:ObjectCreated:*")] }],
        });
        var stored = (await s3.GetBucketNotificationAsync(bucket)).QueueConfigurations ?? [];
        var storedEvents = stored.Select(c => (c.Events ?? []).Select(e => e.Value).ToList()).ToList();
        Ensure(
            storedEvents.Count == 1 && storedEvents[0].SequenceEqual(["s3:ObjectCreated:*"]),
            $"the stored notification configuration is {Py.Repr(storedEvents)}, expected one unfiltered " +
            "s3:ObjectCreated:* subscription — anything narrower gives the absence a second explanation");
        await DrainRecords(ctx, queueUrl, seconds: 8); // discard S3's s3:TestEvent probe message

        // A single part is also the last part, so the 5 MiB minimum part size does not apply.
        var doneKey = $"mpu-completed-{suffix}.bin";
        var (doneId, doneEtag) = await OnePartUpload(ctx, bucket, doneKey);
        var t0 = FidelityTime.StartNew();
        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = doneKey,
            UploadId = doneId,
            PartETags = [new PartETag { PartNumber = 1, ETag = doneEtag }],
        });
        var records = await PollUntil(
            () => DrainRecords(ctx, queueUrl, seconds: 5),
            recs => recs.Any(r => r.Key == doneKey),
            timeoutS: 180,
            intervalS: 0,
            description: $"the COMPLETED multipart upload of {Py.Repr(doneKey)} produced a notification on {queueUrl} — " +
                "the positive control that makes the absence below evidence");
        var latency = Math.Max(t0.Elapsed.TotalSeconds, 1.0);
        var completed = records.Where(r => r.Key == doneKey).ToList();
        Ensure(
            completed.Count == 1,
            $"the completed upload produced {completed.Count} notification record(s) for {Py.Repr(doneKey)}, " +
            $"expected exactly 1: {Render(completed)} — one per CreateMultipartUpload/UploadPart/Complete would " +
            "be three, which is H2's failure mode");
        Ensure(
            completed[0].EventName == "ObjectCreated:CompleteMultipartUpload",
            $"the notification for {Py.Repr(doneKey)} carries eventName={Py.Repr(completed[0].EventName)}, expected " +
            "'ObjectCreated:CompleteMultipartUpload' — a target that reports every creation as " +
            "'ObjectCreated:Put' is exactly what this literal detects");
        Ensure(
            completed[0].Size == Part.Length,
            $"the notification reports size={completed[0].Size?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "None"}, expected {Part.Length} — the assembled " +
            "object's byte count, not the request's");

        var abortKey = $"mpu-aborted-{suffix}.bin";
        var (abortId, _) = await OnePartUpload(ctx, bucket, abortKey);
        var mid = await DrainRecords(ctx, queueUrl, seconds: Math.Max(latency, 10.0));
        Ensure(
            mid.Count == 0,
            $"the queue received {mid.Count} record(s) between CreateMultipartUpload and UploadPart for " +
            $"{Py.Repr(abortKey)}: {Render(mid)} — neither hop creates an object, so neither may notify (H2)");
        await s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = bucket, Key = abortKey, UploadId = abortId });

        var (partsCode, partsMessage) = await ClientError(
            () => s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = abortKey, UploadId = abortId }));
        Ensure(
            partsCode == "NoSuchUpload",
            $"ListParts on the aborted upload {abortId} answered {Py.Repr(partsCode)}, expected 'NoSuchUpload' " +
            "— an upload that still exists was not aborted, and the absence below would be a lost " +
            "notification rather than a withheld one");
        Ensure(
            partsMessage == "The specified upload does not exist. The upload ID may be invalid, or the upload may have " +
                "been aborted or completed.",
            $"ListParts on the aborted upload {abortId} answered message {Py.Repr(partsMessage)}");
        // HEAD responses carry no body, so the client sees the bare status and its reason phrase.
        var (headCode, headMessage) = await ClientError(() => s3.GetObjectMetadataAsync(bucket, abortKey));
        Ensure(
            S3NotificationProbes.IsHeadNotFound(headCode, headMessage),
            $"head_object on {Py.Repr(abortKey)} answered ({Py.Repr(headCode)}, {Py.Repr(headMessage)}), expected " +
            "('404', 'Not Found') — the aborted upload must have assembled no object");
        var liveUploads = ((await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket })).MultipartUploads ?? [])
            .Select(u => u.UploadId)
            .ToList();
        Ensure(
            !liveUploads.Contains(abortId),
            $"ListMultipartUploads still lists the aborted upload {abortId}: {Py.Repr(liveUploads)}");

        // Hold for 3x the measured completion latency before concluding nothing arrives.
        var hold = Math.Max(3 * latency, 15.0);
        var leaked = await DrainRecords(ctx, queueUrl, seconds: hold);
        Ensure(
            leaked.Count == 0,
            $"the queue received {leaked.Count} notification record(s) during a {hold:F0}s hold (3x the " +
            $"{latency:F0}s measured completion latency) for an upload that was ABORTED and assembled no " +
            $"object: {Render(leaked)}");
        var depth = await QueueDepth(ctx, queueUrl);
        Ensure(
            depth == 0,
            $"the queue reports depth {depth} behind an empty receive — the second " +
            "independent observable disagrees with the first");

        // An abort cannot be undone, so a third completed upload shows notifications still work.
        var encoreKey = $"mpu-completed-again-{suffix}.bin";
        var (encoreId, encoreEtag) = await OnePartUpload(ctx, bucket, encoreKey);
        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = encoreKey,
            UploadId = encoreId,
            PartETags = [new PartETag { PartNumber = 1, ETag = encoreEtag }],
        });
        var encore = await PollUntil(
            () => DrainRecords(ctx, queueUrl, seconds: 5),
            recs => recs.Any(r => r.Key == encoreKey),
            timeoutS: 180,
            intervalS: 0,
            description: $"a third, completed upload of {Py.Repr(encoreKey)} still notifies AFTER the abort — the " +
                "emptiness above is the abort's doing, not a dead delivery path");
        var keys = encore.Select(r => r.Key).Order(StringComparer.Ordinal).ToList();
        Ensure(
            keys.SequenceEqual([encoreKey]),
            $"the queue carried {Py.Repr(keys)} on the final leg, expected exactly [{Py.Repr(encoreKey)}] — the " +
            $"aborted upload's key {Py.Repr(abortKey)} must never appear");
    }

    private sealed record Rec(string? EventName, string Key, long? Size);

    private static string Render(List<Rec> recs) =>
        "[" + string.Join(", ", recs.Select(r =>
            $"{{'eventName': {Py.Repr(r.EventName)}, 'key': {Py.Repr(r.Key)}, 'size': {r.Size?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "None"}}}")) + "]";

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
    private static async Task<(string? Code, string? Message)> ClientError(Func<Task> fn)
    {
        var err = await FidelityContext.CaptureError(fn) ?? throw Fail("the call was expected to be rejected but it succeeded");
        return (err.Code, err.Message);
    }

    private static async Task<string> CreateBucket(FidelityContext ctx, string checkTag)
    {
        var s3 = ctx.S3;
        // An INCOMPLETE multipart upload blocks DeleteBucket, so the teardown aborts strays explicitly.
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-nd-abortmpu-", s3);
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

    /// <summary>Every S3 event record seen over <paramref name="seconds"/>, URL-decoded; the s3:TestEvent carries no Records.</summary>
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

    /// <summary>Create a multipart upload and upload exactly one part. Returns (upload_id, etag).</summary>
    private static async Task<(string UploadId, string ETag)> OnePartUpload(FidelityContext ctx, string bucket, string key)
    {
        var s3 = ctx.S3;
        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key })).UploadId;
        var etag = (await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartNumber = 1,
            InputStream = new MemoryStream(Part),
        })).ETag;
        var parts = (await s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId })).Parts ?? [];
        var numbers = parts.Select(p => p.PartNumber ?? 0).ToList();
        Ensure(
            numbers.SequenceEqual([1]),
            $"ListParts for upload {uploadId} on {Py.Repr(key)} reports parts {Py.Repr(numbers)}, expected exactly part 1 — " +
            "the bytes must be shown to have reached S3 before an absence of notification means anything");
        return (uploadId, etag);
    }
}
