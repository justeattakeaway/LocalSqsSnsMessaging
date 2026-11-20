// Translated from behavioral/events__s3__sqs/check_sqs_policy_wrong_source_arn_blocks_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.sqs-policy-wrong-source-arn-blocks-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class SqsPolicyWrongSourceArnBlocksDelivery
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string UnvalidatedDestination = "Unable to validate the following destination configurations";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// A queue policy whose SourceArn names a decoy bucket lets the decoy configure notifications
    /// but refuses the real bucket with InvalidArgument and its exact message, storing no
    /// configuration.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.sqs-policy-wrong-source-arn-blocks-delivery")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await CreateBucket(ctx);
        var decoyBucket = await CreateBucket(ctx);
        var queue = await CreateQueue(ctx);

        Task PutNotification(string bucketName) => s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucketName,
            QueueConfigurations = [new QueueConfiguration { Queue = queue.Arn, Events = ["s3:ObjectCreated:*"] }],
        });

        // Policy is scoped to decoy_bucket's ARN, NOT the real bucket being configured. Neither
        // bucket has ever been validated against this queue, so both attempts get a fresh check.
        await SetQueuePolicy(ctx, queue.Url, SqsPolicyForS3(queue.Arn, decoyBucket.Arn));

        // Twin control, same instant, same fixed policy: the bucket the policy actually names
        // must be authorized — proving the policy is otherwise functional, not just universally
        // rejecting — while the real bucket, named nowhere in the policy, must not be.
        // Positive control: decoy_bucket's own ARN is the policy's SourceArn condition, so its
        // notification config is accepted (a refusal raises here).
        await PutNotification(decoyBucket.Name);

        var err = await ClientError(() => PutNotification(bucket.Name));
        Ensure(
            err == ("InvalidArgument", UnvalidatedDestination),
            $"expected InvalidArgument {Py.Repr(UnvalidatedDestination)}: queue policy SourceArn " +
            "condition references decoy_bucket's ARN, not the real bucket being configured, " +
            $"got {err}");

        // Terminal reads, independent of the mutating calls: decoy_bucket carries the queue
        // config it was authorized for; the real bucket carries none — the rejection was not
        // silently partially applied.
        var decoyLive = await s3.GetBucketNotificationAsync(decoyBucket.Name);
        Ensure(
            decoyLive.QueueConfigurations is { Count: > 0 },
            "decoy_bucket should show the queue configuration its policy authorized: {}");
        var live = await s3.GetBucketNotificationAsync(bucket.Name);
        Ensure(
            live.QueueConfigurations is not { Count: > 0 },
            $"rejected notification config must not have been applied to the real bucket: {Py.Repr(live.QueueConfigurations?.Select(c => c.Queue).ToList())}");
    }

    private sealed record Bucket(string Name, string Arn);

    private sealed record Queue(string Name, string Url, string Arn);

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
