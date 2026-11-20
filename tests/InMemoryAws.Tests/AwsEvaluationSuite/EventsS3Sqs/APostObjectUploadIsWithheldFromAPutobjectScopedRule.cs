// Translated from behavioral/events__s3__sqs/check_a_post_object_upload_is_withheld_from_a_putobject_scoped_rule.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS.Model;
using InMemoryAws.Http;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.a-post-object-upload-is-withheld-from-a-putobject-scoped-rule</summary>
[Category("AwsEvaluationSuite")]
public sealed partial class APostObjectUploadIsWithheldFromAPutobjectScopedRule
{
    private const string Check = "a-post-object-upload-is-withheld-from-a-putobject-scoped-rule";
    private const string Prefix = "nd-post/";
    private const int MaxBytes = 65536;

    /// <summary>
    /// A bucket created moments earlier can answer a POST with <c>307</c> and an <c>&lt;Endpoint&gt;</c>
    /// until its DNS record reaches the Region; the policy carries no Host assertion, so the SAME body
    /// is resubmitted, bounded by a redirect count.
    /// </summary>
    private const int MaxPostRedirects = 5;

    [GeneratedRegex("<Endpoint>([^<]+)</Endpoint>")]
    private static partial Regex EndpointRe();

    /// <summary>
    /// A browser form-POST upload reaches the POST-Object-scoped rule only; the PutObject-scoped rule
    /// on the same bucket receives nothing even though the object was demonstrably created.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.a-post-object-upload-is-withheld-from-a-putobject-scoped-rule")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var events = ctx.Events;
        const string checkTag = Check;
        var suffix = Guid.NewGuid().ToString("N")[..10];

        var bucket = await CreateBucket(ctx, checkTag);
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest { BucketName = bucket, EventBridgeConfiguration = new EventBridgeConfiguration() });
        var stored = await s3.GetBucketNotificationAsync(bucket);
        Ensure(
            stored.EventBridgeConfiguration is not null,
            $"the bucket {bucket} does not report an EventBridgeConfiguration after it was set — no event of either kind would be emitted and both legs below would be vacuous");

        var (putUrl, putArn) = await MakeQueue(ctx, $"sim2real-nd-post-put-{suffix}", checkTag);
        var (postUrl, postArn) = await MakeQueue(ctx, $"sim2real-nd-post-post-{suffix}", checkTag);

        async Task<string> Rule(string namePrefix, string reason, string queueArn)
        {
            var name = FidelityContext.UniqueName(namePrefix);
            await events.PutRuleAsync(new PutRuleRequest
            {
                Name = name,
                EventPattern = new JsonObject
                {
                    ["source"] = new JsonArray("aws.s3"),
                    ["detail-type"] = new JsonArray("Object Created"),
                    ["detail"] = new JsonObject { ["bucket"] = new JsonObject { ["name"] = new JsonArray(bucket) }, ["reason"] = new JsonArray(reason) },
                }.ToJsonString(),
                State = RuleState.ENABLED,
                Tags = [new Amazon.EventBridge.Model.Tag { Key = "sim2real-check", Value = checkTag }],
            });
            ctx.OnCleanup(() => TeardownRule(events, name));
            await PutTargetsOk(events, name, [new Target { Id = "q", Arn = queueArn }]);
            return name;
        }

        var putRule = await Rule("sim2real-nd-post-putrule-", "PutObject", putArn);
        var postRule = await Rule("sim2real-nd-post-postrule-", "POST Object", postArn);

        // BEAT 1 — the PutObject-scoped rule is proven live on this exact bucket, and L is measured.
        var putKey = $"{Prefix}via-put-{suffix}.bin";
        var t0 = FidelityTime.StartNew();
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = putKey, ContentBody = new string('x', 1024) });
        var putEventsSeen = await PollUntil(
            ctx,
            () => Drain(ctx, putUrl, seconds: 6),
            msgs => msgs.Any(m => (string?)m["detail"]!["object"]!["key"] == putKey),
            timeoutS: 180,
            intervalS: 0,
            description: $"the PutObject-scoped rule {putRule} delivered the event for {Py.Repr(putKey)} — the positive control that makes the absence below evidence rather than an artefact");
        var latency = Math.Max(t0.Elapsed.TotalSeconds, 1.0);
        var putEvent = putEventsSeen.First(m => (string?)m["detail"]!["object"]!["key"] == putKey);
        Ensure(
            (string?)putEvent["detail"]!["reason"] == "PutObject",
            $"the PutObject-scoped rule delivered an event whose reason is {Py.Repr((string?)putEvent["detail"]!["reason"])}, expected 'PutObject' — if the reason does not match the " +
            "pattern the rule matched on something else and the disjointness claim is unfounded");
        // The POST-scoped rule must not have matched the PutObject creation either.
        var stray = await Drain(ctx, postUrl, seconds: Math.Max(latency, 10.0));
        Ensure(
            stray.Count == 0,
            $"the POST-Object-scoped rule {postRule} received {stray.Count} event(s) for a plain put_object: " +
            $"{Py.Repr(stray.Select(m => (string?)m["detail"]!["reason"]).ToList())} — its pattern names reason 'POST Object' only");

        // BEAT 2 — the subject: a real signed browser form POST over HTTPS to the bucket endpoint.
        var postKey = $"{Prefix}via-post-{suffix}.bin";
        var payload = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(Guid.NewGuid().ToString("N"), 16)));
        var boundary = $"sim2real{Guid.NewGuid():N}";
        var form = SignedPostForm(ctx, bucket);
        var (status, responseBody, redirectChain) = await HttpPostFollowingRedirect(
            ctx,
            BucketPostUrl(s3, bucket),
            MultipartBody(form, key: postKey, payload: payload, boundary: boundary),
            boundary);
        Ensure(
            status == 201,
            $"the signed POST Object upload of {Py.Repr(postKey)} answered HTTP {status} after following {redirectChain.Count - 1} redirect(s) " +
            $"{Py.Repr(redirectChain)}, expected 201 (success_action_status=201). Body: {Py.Repr(responseBody.Length > 300 ? responseBody[..300] : responseBody)} — " +
            "a failed upload would explain the PutObject queue's emptiness without the reason filter doing anything");
        var head = await s3.GetObjectMetadataAsync(bucket, postKey);
        Ensure(
            head.ContentLength == payload.Length,
            $"head_object reports ContentLength={head.ContentLength} for {Py.Repr(postKey)}, expected {payload.Length} — the object created by the form POST is the SECOND independent observable " +
            "that the creation really happened");

        // The POST-scoped rule receives it, with the documented reason literal.
        var postMsgs = await PollUntil(
            ctx,
            () => Drain(ctx, postUrl, seconds: 6),
            msgs => msgs.Any(m => (string?)m["detail"]!["object"]!["key"] == postKey),
            timeoutS: 180,
            intervalS: 0,
            description: $"the POST-Object-scoped rule {postRule} delivered the event for the form upload {Py.Repr(postKey)}");
        var postEvent = postMsgs.First(m => (string?)m["detail"]!["object"]!["key"] == postKey);
        Ensure(
            (string?)postEvent["detail"]!["reason"] == "POST Object",
            $"the form upload's Object Created event carries reason={Py.Repr((string?)postEvent["detail"]!["reason"])}, expected 'POST Object'. A target that reports every " +
            $"creation as 'PutObject' would have delivered this event to {putRule} instead");
        Ensure(
            (string?)postEvent["detail"]!["bucket"]!["name"] == bucket,
            $"the event names bucket {Py.Repr((string?)postEvent["detail"]!["bucket"]!["name"])}, expected {Py.Repr(bucket)}");

        // BEAT 3 — THE NEGATIVE, held for 3x the measured latency and confirmed by queue depth.
        var hold = Math.Max(3 * latency, 15.0);
        var leaked = await Drain(ctx, putUrl, seconds: hold);
        Ensure(
            leaked.Count == 0,
            $"the PutObject-scoped rule {putRule} received {leaked.Count} event(s) during a {hold:F0}s hold (3x the {latency:F0}s measured S3->EventBridge->SQS latency) for an object created by " +
            $"a form POST: {Py.Repr(leaked.Select(m => ((string?)m["detail"]!["reason"], (string?)m["detail"]!["object"]!["key"])).ToList())}");
        Ensure(
            await QueueDepth(ctx, putUrl) == 0,
            $"the PutObject queue reports depth {await QueueDepth(ctx, putUrl)} behind an empty receive — the second independent observable disagrees with the first");

        // BEAT 4 — the withheld path is not broken, only unmatched: a second put_object on the same
        // bucket still reaches the PutObject rule, so the emptiness above was a filter decision.
        var encoreKey = $"{Prefix}via-put-again-{suffix}.bin";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = encoreKey, ContentBody = new string('y', 512) });
        var encore = await PollUntil(
            ctx,
            () => Drain(ctx, putUrl, seconds: 6),
            msgs => msgs.Any(m => (string?)m["detail"]!["object"]!["key"] == encoreKey),
            timeoutS: 180,
            intervalS: 0,
            description: $"the PutObject-scoped rule {putRule} is still live AFTER the withheld form upload — the emptiness above is a reason mismatch, not a dead delivery path");
        var keys = encore.Select(m => (string?)m["detail"]!["object"]!["key"]).Order(StringComparer.Ordinal).ToList();
        Ensure(
            keys.SequenceEqual([encoreKey]),
            $"the PutObject queue carried {Py.Repr(keys)} on the final leg, expected exactly [{Py.Repr(encoreKey)}] — the form upload's event must not arrive late");
    }

    /// <summary>A bounded retry that RAISES on its deadline; never returns its last value on timeout.</summary>
    private static async Task<T> PollUntil<T>(FidelityContext ctx, Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, double intervalS = 3.0, string description = "condition")
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

            await ctx.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not met within {timeoutS}s. Last result: {Py.Repr(last)}");
    }

    /// <summary>A queue any EventBridge rule in this account may send to.</summary>
    private static async Task<(string Url, string Arn)> MakeQueue(FidelityContext ctx, string name, string checkTag)
    {
        var sqs = ctx.Sqs;
        var account = ctx.Account;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = new() { ["sim2real-check"] = checkTag } })).QueueUrl;
        ctx.OnCleanup(() => ctx.Sqs.DeleteQueueAsync(url));
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
                        ["Condition"] = new JsonObject { ["ArnLike"] = new JsonObject { ["aws:SourceArn"] = $"arn:*:events:{ctx.Region}:{account}:rule/*" } },
                    }),
                }.ToJsonString(),
            },
        });
        return (url, arn);
    }

    /// <summary>Receive and delete every message visible within <paramref name="seconds"/>; return their parsed bodies.</summary>
    private static async Task<List<JsonObject>> Drain(FidelityContext ctx, string url, double seconds)
    {
        var sqs = ctx.Sqs;
        var output = new List<JsonObject>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed.TotalSeconds < seconds)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                output.Add(JsonNode.Parse(msg.Body)!.AsObject());
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
        return attrs.Values.Sum(v => int.Parse(v, CultureInfo.InvariantCulture));
    }

    private static async Task<string> CreateBucket(FidelityContext ctx, string checkTag)
    {
        var s3 = ctx.S3;
        var name = FidelityContext.UniqueName("sim2real-nd-post-");
        var request = new PutBucketRequest { BucketName = name, UseClientRegion = false };
        if (ctx.Region != "us-east-1")
        {
            // region-pinned API quirk, not a hardcoded region
            request.PutBucketConfiguration = new PutBucketConfiguration { LocationConstraint = new BucketLocationConstraint(ctx.Region) };
        }

        await s3.PutBucketAsync(request);
        ctx.OnCleanup(() => DeleteBucket(s3, name));
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = name,
            TagSet =
            [
                new Amazon.S3.Model.Tag { Key = "sim2real:managed", Value = "true" },
                new Amazon.S3.Model.Tag { Key = "sim2real-check", Value = checkTag },
            ],
        });
        return name;
    }

    private static async Task DeleteBucket(IAmazonS3 s3, string name)
    {
        var versions = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = name });
        var stale = (versions.Versions ?? []).Select(o => new KeyVersion { Key = o.Key, VersionId = o.VersionId }).ToList();
        if (stale.Count > 0)
        {
            var resp = await s3.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = name, Objects = stale });
            if (resp.DeleteErrors is { Count: > 0 } failed)
            {
                throw new InvalidOperationException($"DeleteObjects left {failed.Count} object(s)");
            }
        }

        await s3.DeleteBucketAsync(name);
    }

    /// <summary>
    /// The POST Object target, derived FROM THE TARGET so it follows whichever addressing style the
    /// endpoint uses. Transport derivation only — no assertion forks on it.
    /// </summary>
    private static string BucketPostUrl(AmazonS3Client s3, string bucket)
    {
        const string probe = "post-object-endpoint-probe";
        var presigned = s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = probe,
            Verb = HttpVerb.PUT,
            Expires = FidelityTime.UtcNow.UtcDateTime.AddSeconds(60),
        });
        var baseUrl = presigned.Split('?', 2)[0];
        Ensure(baseUrl.EndsWith("/" + probe, StringComparison.Ordinal), $"cannot derive the bucket URL: the presigned PutObject URL {Py.Repr(baseUrl)} does not end in /{probe}");
        return baseUrl[..^probe.Length];
    }

    private static byte[] Sign(byte[] key, string msg) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(msg));

    /// <summary>
    /// A SigV4-signed POST Object policy, recomputed from the LIVE clock and ctx's own credentials
    /// (every client signs with access key and secret <c>test</c>).
    /// </summary>
    private static List<(string Name, string Value)> SignedPostForm(FidelityContext ctx, string bucket)
    {
        const string accessKey = "test";
        const string secretKey = "test";
        var now = FidelityTime.UtcNow.UtcDateTime;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var datestamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var expiration = now.AddMinutes(20).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var credential = $"{accessKey}/{datestamp}/{ctx.Region}/s3/aws4_request";
        var conditions = new JsonArray(
            new JsonObject { ["bucket"] = bucket },
            new JsonArray("starts-with", "$key", Prefix),
            new JsonObject { ["x-amz-algorithm"] = "AWS4-HMAC-SHA256" },
            new JsonObject { ["x-amz-credential"] = credential },
            new JsonObject { ["x-amz-date"] = amzDate },
            new JsonObject { ["success_action_status"] = "201" },
            new JsonArray("content-length-range", 1, MaxBytes));
        var policy = Convert.ToBase64String(Encoding.UTF8.GetBytes(new JsonObject { ["expiration"] = expiration, ["conditions"] = conditions }.ToJsonString()));
        var signingKey = Sign(Sign(Sign(Sign(Encoding.UTF8.GetBytes($"AWS4{secretKey}"), datestamp), ctx.Region), "s3"), "aws4_request");
        return
        [
            ("x-amz-algorithm", "AWS4-HMAC-SHA256"),
            ("x-amz-credential", credential),
            ("x-amz-date", amzDate),
            ("success_action_status", "201"),
            ("policy", policy),
            ("x-amz-signature", Convert.ToHexStringLower(Sign(signingKey, policy))),
        ];
    }

    /// <summary>
    /// A real browser-shaped multipart/form-data body. <c>key</c> must precede <c>file</c>, as POST
    /// Object ignores every field after <c>file</c>.
    /// </summary>
    private static byte[] MultipartBody(List<(string Name, string Value)> fields, string key, byte[] payload, string boundary)
    {
        using var output = new MemoryStream();
        void Write(string text) => output.Write(Encoding.UTF8.GetBytes(text));
        foreach (var (name, value) in fields.Append(("key", key)))
        {
            Write($"--{boundary}\r\n");
            Write($"Content-Disposition: form-data; name=\"{name}\"\r\n\r\n");
            Write($"{value}\r\n");
        }

        Write($"--{boundary}\r\n");
        Write("Content-Disposition: form-data; name=\"file\"; filename=\"upload.bin\"\r\nContent-Type: application/octet-stream\r\n\r\n");
        output.Write(payload);
        Write($"\r\n--{boundary}--\r\n");
        return output.ToArray();
    }

    /// <summary>Returns (status, body) for both success and HTTP error, so a 4xx is data rather than an exception.</summary>
    private static async Task<(int Status, string Body)> HttpPost(FidelityContext ctx, string url, byte[] body, string boundary)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");
        using var response = await ctx.RawHttpClient(AwsServiceType.S3).PostAsync(new Uri(url), content);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// <see cref="HttpPost"/>, but a <c>307</c> naming a replacement <c>&lt;Endpoint&gt;</c> is followed,
    /// not treated as the answer. Raises if the chain does not settle within <see cref="MaxPostRedirects"/> hops.
    /// </summary>
    private static async Task<(int Status, string Body, List<string> Chain)> HttpPostFollowingRedirect(FidelityContext ctx, string url, byte[] body, string boundary)
    {
        var chain = new List<string> { url };
        for (var i = 0; i < MaxPostRedirects; i++)
        {
            var (status, responseBody) = await HttpPost(ctx, url, body, boundary);
            if (status != (int)HttpStatusCode.TemporaryRedirect)
            {
                return (status, responseBody, chain);
            }

            var match = EndpointRe().Match(responseBody);
            Ensure(match.Success, $"POST to {url} answered 307 with no <Endpoint> to follow: {Py.Repr(responseBody.Length > 300 ? responseBody[..300] : responseBody)}");
            url = $"https://{match.Groups[1].Value}/";
            chain.Add(url);
        }

        throw Fail($"POST Object kept redirecting past {MaxPostRedirects} hops: {Py.Repr(chain)}");
    }

    private static async Task PutTargetsOk(AmazonEventBridgeClient events, string rule, List<Target> targets)
    {
        var resp = await events.PutTargetsAsync(new PutTargetsRequest { Rule = rule, Targets = targets });
        Ensure(
            resp.FailedEntryCount == 0,
            $"put_targets on {rule} reported failures, so the wiring under test does not exist: {Py.Repr(resp.FailedEntries?.Select(f => f.ErrorMessage).ToList())}");
    }

    private static async Task TeardownRule(AmazonEventBridgeClient events, string rule)
    {
        await events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, Ids = ["q"], Force = true });
        await events.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, Force = true });
    }
}
