// Translated from behavioral/s3__sns__sqs/check_s3_notification_without_topic_policy_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using Amazon.SimpleNotificationService.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3SnsSqs;

/// <summary>s3__sns__sqs.s3-notification-without-topic-policy-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3NotificationWithoutTopicPolicyRejected
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// S3 validates the destination at configuration time: a bucket notification to a topic whose
    /// policy denies s3.amazonaws.com publish is refused with InvalidArgument and not applied, while
    /// the identical call succeeds before revoking and after restoring the permission.
    /// </summary>
    [Test]
    [DisplayName("s3__sns__sqs.s3-notification-without-topic-policy-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var bucket = await CreateBucket(ctx);
        var topic = await CreateTopic(ctx);
        var s3 = ctx.S3;
        PutBucketNotificationRequest NotifConfig() => new()
        {
            BucketName = bucket.Name,
            TopicConfigurations = [new TopicConfiguration { Topic = topic.Arn, Events = ["s3:ObjectCreated:*"] }],
        };

        // Positive control: with a permitting policy, the identical call is accepted.
        await SetTopicPolicy(ctx, topic, bucket.Arn, s3MayPublish: true);
        await s3.PutBucketNotificationAsync(NotifConfig());
        var configured = await ConfiguredTopicArns(ctx, bucket.Name);
        Ensure(
            configured.SequenceEqual([topic.Arn]),
            $"positive control failed: expected [{topic.Arn}] configured, got {Py.Repr(configured)}");

        // Break exactly one thing: revoke s3.amazonaws.com's publish permission on the topic, and
        // clear the bucket's config first so any later presence can only come from the broken call.
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest { BucketName = bucket.Name });
        await SetTopicPolicy(ctx, topic, bucket.Arn, s3MayPublish: false);
        // botocore reports ArgumentName1/ArgumentValue1, which the .NET SDK drops, so this call goes
        // over raw HTTP to read the same error document.
        var err = await S3Fidelity.PutNotificationConfigurationRaw(
            ctx, bucket.Name, S3Fidelity.TopicConfigurationXml(topic.Arn, ["s3:ObjectCreated:*"]));
        if (err is null)
        {
            throw Fail("S3 accepted a notification to a topic S3 cannot publish to (over-permissive)");
        }

        var status = (int)err.StatusCode;
        Ensure(
            err.Code == "InvalidArgument" && status == 400,
            $"expected InvalidArgument HTTP 400, got {Py.Repr(err.Code)} HTTP {status}");
        const string expectedMessage = "Unable to validate the following destination configurations";
        Ensure(err.Message == expectedMessage, $"expected the message {Py.Repr(expectedMessage)}, got {Py.Repr(err.Message)}");
        // S3 names the refused destination in ArgumentName1, not in the message text.
        Ensure(
            err.Get("ArgumentName1") == topic.Arn,
            $"the rejection names destination {Py.Repr(err.Get("ArgumentName1"))}, expected {Py.Repr(topic.Arn)}");
        Ensure(!string.IsNullOrWhiteSpace(err.Get("ArgumentValue1")), $"expected a non-empty per-destination reason: {err}");

        // Effect absent, via an independent read: the rejected call must not have taken effect.
        configured = await ConfiguredTopicArns(ctx, bucket.Name);
        Ensure(configured.Count == 0, $"rejected notification config was applied anyway: topics={Py.Repr(configured)}");

        // Restore: re-permit, and the identical call now succeeds.
        await SetTopicPolicy(ctx, topic, bucket.Arn, s3MayPublish: true);
        await s3.PutBucketNotificationAsync(NotifConfig());
        configured = await ConfiguredTopicArns(ctx, bucket.Name);
        Ensure(
            configured.SequenceEqual([topic.Arn]),
            $"restore failed: expected [{topic.Arn}] configured after re-permitting, got {Py.Repr(configured)}");

        // Tear down in reverse dependency order and prove each resource gone.
        await S3SnsTeardown.GoneBucket(ctx, bucket.Name);
        await S3SnsTeardown.GoneTopic(ctx, topic.Arn);
    }

    private sealed record Bucket(string Name, string Arn);

    private sealed record Topic(string Name, string Arn);

    private static Dictionary<string, string> XsvcResourceTags() =>
        new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>Create an SNS topic (registers delete_topic teardown). Returns {name,arn}.</summary>
    private static async Task<Topic> CreateTopic(FidelityContext ctx)
    {
        var name = XName("topic");
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = name,
            Tags = [.. XsvcResourceTags().Select(t => new Amazon.SimpleNotificationService.Model.Tag { Key = t.Key, Value = t.Value })],
        })).TopicArn;
        ctx.OnCleanup(S3SnsTeardown.Tolerant(() => sns.DeleteTopicAsync(arn), "NotFound"));
        return new Topic(name, arn);
    }

    /// <summary>Create an S3 bucket (registers empty-then-delete teardown). Returns {name,arn}.</summary>
    private static async Task<Bucket> CreateBucket(FidelityContext ctx)
    {
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, $"{XsvcPrefix}bkt-", s3);
        await FidelityContext.CaptureError(() => s3.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = name,
            TagSet = [.. XsvcResourceTags().Select(t => new Amazon.S3.Model.Tag { Key = t.Key, Value = t.Value })],
        }));
        return new Bucket(name, $"arn:aws:s3:::{name}");
    }

    /// <summary>
    /// Set the topic's policy to owner-only, optionally adding the statement that lets
    /// s3.amazonaws.com publish to it for this specific bucket.
    /// </summary>
    private static Task SetTopicPolicy(FidelityContext ctx, Topic topic, string bucketArn, bool s3MayPublish)
    {
        var account = topic.Arn.Split(':')[4];
        var statements = new JsonArray(new JsonObject
        {
            ["Sid"] = "OwnerOnly",
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["AWS"] = $"arn:aws:iam::{account}:root" },
            ["Action"] = "sns:Publish",
            ["Resource"] = topic.Arn,
        });
        if (s3MayPublish)
        {
            statements.Add(new JsonObject
            {
                ["Sid"] = "AllowS3",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "s3.amazonaws.com" },
                ["Action"] = "sns:Publish",
                ["Resource"] = topic.Arn,
                ["Condition"] = new JsonObject
                {
                    ["ArnLike"] = new JsonObject { ["aws:SourceArn"] = bucketArn },
                    ["StringEquals"] = new JsonObject { ["aws:SourceAccount"] = account },
                },
            });
        }

        var policy = new JsonObject { ["Version"] = "2012-10-17", ["Statement"] = statements };
        return ctx.Sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = topic.Arn,
            AttributeName = "Policy",
            AttributeValue = policy.ToJsonString(),
        });
    }

    /// <summary>
    /// Independent read of the bucket's live notification configuration (never the mutating call's
    /// own response).
    /// </summary>
    private static async Task<List<string>> ConfiguredTopicArns(FidelityContext ctx, string bucket)
    {
        var got = await ctx.S3.GetBucketNotificationAsync(bucket);
        return (got.TopicConfigurations ?? []).Select(c => c.Topic).ToList();
    }
}
