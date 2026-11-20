// Translated from behavioral/events__s3__sqs/check_s3_notification_without_queue_policy_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.s3-notification-without-queue-policy-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3NotificationWithoutQueuePolicyFails
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string UnvalidatedDestination = "Unable to validate the following destination configurations";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// PutBucketNotificationConfiguration with a queue lacking a policy fails with InvalidArgument
    /// and its exact message and leaves the stored configuration unchanged; once granted the call
    /// succeeds.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.s3-notification-without-queue-policy-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await CreateBucket(ctx);
        var okQueue = await CreateQueue(ctx);
        var badQueue = await CreateQueue(ctx); // deliberately never given a policy

        Task Put(Queue queue) => s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket.Name,
            QueueConfigurations = [new QueueConfiguration { Queue = queue.Arn, Events = ["s3:ObjectCreated:*"] }],
        });

        // Positive control: a queue whose policy grants s3.amazonaws.com is accepted, and the
        // config is visible at the destination via an independent read.
        await SetQueuePolicy(ctx, okQueue.Url, SqsPolicyForS3(okQueue.Arn, bucket.Arn));
        await Put(okQueue);
        Ensure(
            (await ConfiguredQueueArns(ctx, bucket.Name)).SequenceEqual([okQueue.Arn]),
            "positive control failed: notification config did not take effect with a " +
            "permissioned queue policy");

        // Break exactly one thing: point at a queue that has no permission policy at all.
        var err = await ClientError(() => Put(badQueue));
        Ensure(
            err == ("InvalidArgument", UnvalidatedDestination),
            $"expected InvalidArgument {Py.Repr(UnvalidatedDestination)} when the queue policy grants no " +
            $"permission, got {err}");
        Ensure(
            (await ConfiguredQueueArns(ctx, bucket.Name)).SequenceEqual([okQueue.Arn]),
            "the rejected notification config must not take effect at the destination — the " +
            "prior (positive-control) config must still be the one in force");

        // Restore: grant the policy and prove the IDENTICAL call now succeeds.
        await SetQueuePolicy(ctx, badQueue.Url, SqsPolicyForS3(badQueue.Arn, bucket.Arn));
        await Put(badQueue);
        Ensure(
            (await ConfiguredQueueArns(ctx, bucket.Name)).SequenceEqual([badQueue.Arn]),
            "restore failed: the identical call did not succeed once the queue policy was granted");
    }

    private sealed record Bucket(string Name, string Arn);

    private sealed record Queue(string Name, string Url, string Arn);

    private static async Task<List<string>> ConfiguredQueueArns(FidelityContext ctx, string bucket)
    {
        var resp = await ctx.S3.GetBucketNotificationAsync(bucket);
        return (resp.QueueConfigurations ?? []).Select(q => q.Queue).ToList();
    }

    private static JsonObject SqsPolicyForS3(string queueArn, string bucketArn) => new()
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

    private static Task SetQueuePolicy(FidelityContext ctx, string queueUrl, JsonObject policy) =>
        ctx.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new() { ["Policy"] = policy.ToJsonString() },
        });

    private static Dictionary<string, string> XsvcResourceTags() =>
        new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>(error code, message) of a call expected to fail; RAISES if the call succeeds.</summary>
    private static async Task<(string?, string?)> ClientError(Func<Task> fn)
    {
        var err = await FidelityContext.CaptureError(fn)
            ?? throw Fail("the call was expected to be refused but succeeded: {}");
        return (err.Code, err.Message);
    }

    /// <summary>Create an SQS queue (registers delete_queue teardown). Returns {name,url,arn}.</summary>
    private static async Task<Queue> CreateQueue(FidelityContext ctx)
    {
        var name = XName("q");
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = [], Tags = XsvcResourceTags() })).QueueUrl;
        var arn = await S3Fidelity.NotificationQueueArn(sqs, url);
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        return new Queue(name, url, arn);
    }

    /// <summary>Create an S3 bucket (registers empty-then-delete teardown). Returns {name,arn}.</summary>
    private static async Task<Bucket> CreateBucket(FidelityContext ctx)
    {
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, $"{XsvcPrefix}bkt-", s3);
        await FidelityContext.CaptureError(() => s3.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = name,
            TagSet = [.. XsvcResourceTags().Select(t => new Tag { Key = t.Key, Value = t.Value })],
        }));
        return new Bucket(name, $"arn:aws:s3:::{name}");
    }
}
