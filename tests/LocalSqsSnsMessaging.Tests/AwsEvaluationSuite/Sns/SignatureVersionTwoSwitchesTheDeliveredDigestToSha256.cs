// Translated from behavioral/sns/check_signature_version_two_switches_the_delivered_digest_to_sha256.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.signature-version-two-switches-the-delivered-digest-to-sha256</summary>
[Category("AwsEvaluationSuite")]
public sealed class SignatureVersionTwoSwitchesTheDeliveredDigestToSha256
{
    private const string Scenario = "sns-sigv2";

    // The digest each SNS SignatureVersion selects for RSASSA-PKCS1-v1_5: version 1 signs with
    // SHA-1, version 2 with SHA-256.
    private static readonly Dictionary<string, HashAlgorithmName> Digests = new()
    {
        ["1"] = HashAlgorithmName.SHA1,
        ["2"] = HashAlgorithmName.SHA256,
    };

    /// <summary>
    /// Setting topic SignatureVersion 2 makes delivered notifications verify under SHA-256 and not
    /// SHA-1, setting 1 reverses that, and SignatureVersion 3 is refused without changing the value.
    /// Reference: https://docs.aws.amazon.com/sns/latest/dg/sns-verify-signature-of-message.html
    /// </summary>
    [Test]
    [DisplayName("sns.signature-version-two-switches-the-delivered-digest-to-sha256")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsEnvelope, "The delivered envelope's SigningCertURL is the placeholder 'EXAMPLE', not an http(s) URL serving a certificate")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var (queueUrl, queueArn) = await CreateQueueForTopic(ctx, topicArn);
        // NOT raw delivery: the Signature, SigningCertURL and SignatureVersion live in the JSON
        // notification envelope, which raw delivery strips.
        await SubscribeAndConfirm(ctx, topicArn, queueArn);

        var declaredDefault = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn }))
            .Attributes.GetValueOrDefault("SignatureVersion", "1");
        Ensure(declaredDefault == "1",
            "a freshly created topic must declare SignatureVersion 1 (documented default); got " +
            $"'{declaredDefault}' — every leg below is relative to this baseline");

        await AssertSignedWith(ctx, sns, topicArn, queueUrl, expectVersion: "1");

        await SetSignatureVersion(sns, topicArn, "2");
        await AssertSignedWith(ctx, sns, topicArn, queueUrl, expectVersion: "2");

        await SetSignatureVersion(sns, topicArn, "1");
        await AssertSignedWith(ctx, sns, topicArn, queueUrl, expectVersion: "1");

        var (code, message) = await Error(() => sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = topicArn, AttributeName = "SignatureVersion", AttributeValue = "3",
        }));
        Ensure(code == "InvalidParameter",
            "expected SetTopicAttributes(SignatureVersion=3) to be refused with Code " +
            $"'InvalidParameter' — the documented value set is {{1, 2}}; got Code '{code}' and " +
            $"Message '{message}'");
        Ensure(message == "Invalid parameter: SignatureVersion: Invalid value '[3]'. Must be 1 or 2.",
            $"expected the exact message \"Invalid parameter: SignatureVersion: Invalid value '[3]'. Must be 1 or 2.\", got '{message}'");
        var after = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn }))
            .Attributes.GetValueOrDefault("SignatureVersion");
        Ensure(after == "1",
            "a refused SetTopicAttributes must not change the stored SignatureVersion: expected " +
            $"'1', got '{after}'");
    }

    /// <summary>Set the topic's SignatureVersion and poll an independent read until it reflects it.</summary>
    private static async Task SetSignatureVersion(AmazonSimpleNotificationServiceClient sns, string topicArn, string version)
    {
        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest { TopicArn = topicArn, AttributeName = "SignatureVersion", AttributeValue = version });
        await PollUntil(
            async () => (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn })).Attributes.GetValueOrDefault("SignatureVersion"),
            value => value == version,
            timeoutS: 60.0,
            intervalS: 2.0,
            description: $"GetTopicAttributes reflects SignatureVersion={version}");
    }

    /// <summary>
    /// Publish, then require the delivered notification to verify under expect_version's
    /// digest and to fail under the other version's digest.
    /// </summary>
    private static async Task AssertSignedWith(FidelityContext ctx, AmazonSimpleNotificationServiceClient sns, string topicArn, string queueUrl, string expectVersion)
    {
        var marker = FidelityContext.UniqueName($"sim2real-behav-sns-sigv{expectVersion}-");
        var subject = $"sim2real-sigv{expectVersion}";
        await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Subject = subject, Message = marker });
        var envelope = await AwaitEnvelope(ctx, queueUrl, marker, timeoutS: 120.0,
            description: $"the notification published under SignatureVersion={expectVersion}");

        var gotVersion = Field(envelope, "SignatureVersion");
        Ensure(gotVersion == expectVersion,
            $"expected the delivered envelope to report SignatureVersion '{expectVersion}' " +
            $"after the topic attribute was set to it; got '{gotVersion}'");
        Ensure(Field(envelope, "Subject") == subject,
            $"expected the Subject to survive delivery as '{subject}', got " +
            $"'{Field(envelope, "Subject")}' — the Subject is part of the signed canonical string");

        var certUrl = Field(envelope, "SigningCertURL");
        Uri.TryCreate(certUrl, UriKind.Absolute, out var parsed);
        Ensure(parsed is not null && (parsed.Scheme == "http" || parsed.Scheme == "https"), $"SigningCertURL is not http(s): '{certUrl}'");
        Ensure(!string.IsNullOrEmpty(parsed.Host), $"SigningCertURL names no host: '{certUrl}'");
        // A raw HTTPS GET to the host the envelope itself named.
        using var resp = await ctx.GetPublishedUrl(certUrl);
        resp.EnsureSuccessStatusCode();
        var pem = await resp.Content.ReadAsStringAsync();
        Ensure(pem.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal),
            $"SigningCertURL did not serve a PEM certificate: '{(pem.Length <= 120 ? pem : pem[..120])}'");
        using var certificate = X509Certificate2.CreateFromPem(pem);
        using var rsa = certificate.GetRSAPublicKey()
            ?? throw Fail("no RSA public key found in the certificate served by SigningCertURL");

        var canonical = Encoding.UTF8.GetBytes(CanonicalString(envelope));
        var signature = Convert.FromBase64String(Field(envelope, "Signature"));

        var matching = rsa.VerifyData(canonical, signature, Digests[expectVersion], RSASignaturePadding.Pkcs1);
        Ensure(matching,
            $"the Signature on a notification delivered while SignatureVersion={expectVersion} " +
            $"must verify under {Digests[expectVersion].Name}; RSASSA-PKCS1-v1_5 " +
            $"verification failed over {canonical.Length} canonical bytes with a " +
            $"{rsa.KeySize}-bit key");
        var other = expectVersion == "2" ? "1" : "2";
        var crossed = rsa.VerifyData(canonical, signature, Digests[other], RSASignaturePadding.Pkcs1);
        Ensure(!crossed,
            $"a notification signed under SignatureVersion={expectVersion} must NOT verify under " +
            $"version {other}'s digest ({Digests[other].Name}); it did, which means " +
            "the digest is not actually selected by the attribute (or the verifier accepts " +
            "anything)");
    }

    /// <summary><c>envelope[key]</c>: a missing key raises, as the Python dict lookup does.</summary>
    private static string Field(JsonObject envelope, string key) =>
        envelope.TryGetPropertyValue(key, out var value)
            ? value?.ToString() ?? "None"
            : throw new KeyNotFoundException($"'{key}'");

    /// <summary>
    /// Long-poll until a notification envelope whose Message is exactly <c>marker</c> arrives; raise
    /// on the deadline.
    /// </summary>
    private static async Task<JsonObject> AwaitEnvelope(FidelityContext ctx, string queueUrl, string marker, double timeoutS, string description)
    {
        var sqs = ctx.Sqs;
        var deadline = FidelityTime.StartNew();
        var others = new List<string>();
        while (true)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var envelope = JsonNode.Parse(msg.Body)!.AsObject();
                var inner = envelope["Message"]?.ToString();
                if (inner == marker)
                {
                    return envelope;
                }

                var text = inner ?? "None";
                others.Add(text.Length <= 60 ? text : text[..60]);
            }

            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: no envelope with Message == '{marker}' arrived within " +
                    $"{timeoutS}s; bodies seen: [{string.Join(", ", others.Select(o => $"'{o}'"))}]");
            }
        }
    }

    /// <summary>
    /// The string SNS signs for a Notification: field\nvalue\n per signed key, in the
    /// documented order.
    /// </summary>
    private static string CanonicalString(JsonObject envelope)
    {
        string[] order = ["Message", "MessageId", "Subject", "Timestamp", "TopicArn", "Type"];
        var parts = new StringBuilder();
        foreach (var key in order)
        {
            if (key == "Subject" && !envelope.ContainsKey("Subject"))
            {
                continue;
            }

            parts.Append(key).Append('\n').Append(Field(envelope, key)).Append('\n');
        }

        return parts.ToString();
    }

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var arn = (await ctx.Sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = [], Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic publish to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => ctx.Sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topicArn } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (url, arn);
    }

    /// <summary>Subscribe an SQS queue and poll GetSubscriptionAttributes until it is confirmed.</summary>
    private static async Task<string> SubscribeAndConfirm(FidelityContext ctx, string topicArn, string queueArn,
        Dictionary<string, string>? attributes = null, double timeoutS = 60.0)
    {
        var sns = ctx.Sns;
        var request = new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn, ReturnSubscriptionArn = true };
        if (attributes is { Count: > 0 })
        {
            request.Attributes = attributes;
        }

        var subArn = (await sns.SubscribeAsync(request)).SubscriptionArn;
        ctx.OnCleanup(() => ctx.Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        await PollUntil(
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("PendingConfirmation"),
            pending => pending == "false",
            timeoutS: timeoutS,
            intervalS: 2.0,
            description: $"subscription {subArn} leaves PendingConfirmation");
        return subArn;
    }

    /// <summary>Bounded retry; raise on the deadline.</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS, string description)
    {
        var deadline = FidelityTime.StartNew();
        var value = await fn();
        while (!pred(value))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: not satisfied within {timeoutS}s, last value '{value}'");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            value = await fn();
        }

        return value;
    }

    /// <summary>(Code, Message) for a refused call; raise on unexpected success.</summary>
    private static async Task<(string? Code, string Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is null)
        {
            throw Fail("expected this call to be refused, but it succeeded; an over-permissive target must " +
                "not be able to report a missing error code instead of an unexpected success");
        }

        return (error.Code, error.Message ?? "");
    }
}
